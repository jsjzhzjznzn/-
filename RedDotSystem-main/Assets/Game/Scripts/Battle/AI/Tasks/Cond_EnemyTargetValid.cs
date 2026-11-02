using BehaviorDesigner.Runtime.Tasks;

/// <summary>
/// 有交战目标：目标存在且在感知距离内。
///
/// 刻意**不要求"当前有视线"**：视野角之外的玩家不该让 BOSS 变木桩（玩家绕到背后就站住不动），
/// 追击分支会边追边转，朝向对上之后自然恢复视线。视线只在"是否允许出手"里用
/// （见 Cond_EnemyInAttackRange），这样既不给木桩，也不会隔墙挥刀。
/// </summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("目标存在且在感知距离内（不要求当前视线）")]
public class Cond_EnemyTargetValid : Conditional
{
    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (brain.Target == null || brain.Profile == null) return TaskStatus.Failure;
        return brain.TargetDistance <= brain.Profile.sightRange ? TaskStatus.Success : TaskStatus.Failure;
    }
}
