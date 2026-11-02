using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 概率条件：给"环绕 / 后撤"这类可选行为加随机性，避免 AI 每次都做同一件事。
/// 每次被求值掷一次骰子（一条 Sequence 每轮只求值一次，不会逐帧抖动）。
/// </summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("按概率返回成功（用于给可选行为加随机性）")]
public class Cond_EnemyRandom : Conditional
{
    /// <summary>成功概率 0~1</summary>
    [Range(0f, 1f)] public float chance = 0.5f;

    public override TaskStatus OnUpdate()
    {
        return Random.value < Mathf.Clamp01(chance) ? TaskStatus.Success : TaskStatus.Failure;
    }
}
