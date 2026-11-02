using UnityEngine;

/// <summary>
/// 动作层控制器：整个敌人唯一"谁在动身体"的仲裁者。
///
/// 职责边界：
///  - 持有当前动作，逐帧 Tick；
///  - 新动作到来时问当前动作"你能被打断吗"（打断矩阵由动作自己判断）；
///  - 动作结束时派发 ActionFinished，供决策层（行为树的 WaitActionEnd）继续。
///
/// 它不做的事：不选招、不追人、不结算伤害——那些属于决策层与动作本身。
/// </summary>
[DisallowMultipleComponent]
public class EnemyActionController : MonoBehaviour
{
    private EnemyAction current;
    private EnemyBrain brain;

    private void Awake()
    {
        brain = GetComponent<EnemyBrain>();
    }

    /// <summary>当前动作（可能为 null）</summary>
    public EnemyAction Current => current;

    /// <summary>当前没有动作（可以接受新决策）</summary>
    public bool IsIdle => current == null || current.IsFinished;

    /// <summary>当前动作种类（日志/断言用）</summary>
    public EnemyActionKind CurrentKind => current != null ? current.Kind : EnemyActionKind.None;

    /// <summary>
    /// 尝试开始一个动作。
    /// 返回 false = 当前动作处于霸体/判定中，决策层应当换分支（而不是每 tick 重试）。
    /// </summary>
    public bool TryStart(EnemyAction next)
    {
        if (next == null) return false;

        if (current != null && !current.IsFinished && !current.CanBeInterruptedBy(next))
        {
            return false;
        }

        EnemyAction previous = current;
        previous?.Exit();
        current = next;
        current.Enter();

        if (previous != null && previous.Kind != next.Kind && brain != null)
        {
            brain.OnActionSwitched(previous.Kind, next.Kind);
        }
        return true;
    }

    /// <summary>强制结束当前动作（死亡/重开时用）</summary>
    public void ForceStop()
    {
        current?.Exit();
        current = null;
    }

    private void Update()
    {
        if (current == null) return;

        current.Tick(Time.deltaTime);

        if (current.IsFinished)
        {
            EnemyAction finished = current;
            EnemyActionKind finishedKind = finished.Kind;

            // 连段：动作自己已经掷好下一招，同帧续接，不经过决策层（避免 0.15s 的树 tick 空档）
            EnemyAction chained = finished.Next;

            finished.Exit();
            current = null;
            if (brain != null) brain.OnActionFinished(finishedKind);

            if (chained != null)
            {
                current = chained;
                chained.Enter();
                if (brain != null && chained.Kind != finishedKind) brain.OnActionSwitched(finishedKind, chained.Kind);
            }
        }
    }
}
