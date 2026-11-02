/// <summary>
/// 动作层基类：一次"身体行为"的完整生命周期。
///
/// 约定：
///  - Enter 只做一次（播动画/清事件/开窗口），Tick 逐帧推进，Exit 收尾（清事件/复位）；
///  - 是否结束由动作自己判定（计时器/动画长度），**不要依赖 Animancer 的 Events.OnEnd**
///    （多处回调互相覆盖是旧 ChallengeMonster 状态卡死的根因）；
///  - CanBeInterruptedBy 表达"打断矩阵"，由动作自己看当前阶段，控制器只负责询问。
/// </summary>
public abstract class EnemyAction
{
    protected readonly EnemyBrain brain;

    protected EnemyAction(EnemyBrain brain)
    {
        this.brain = brain;
    }

    public abstract EnemyActionKind Kind { get; }

    public virtual bool IsFinished { get; protected set; }

    /// <summary>动作进度 0~1（没有时间概念的动作返回 0）</summary>
    public virtual float NormalizedTime => 0f;

    /// <summary>
    /// 连段续接目标：动作在这个 tick 里被判定结束时，控制器会直接接管它，
    /// 不回到决策层。null = 正常结束（交回行为树）。
    /// </summary>
    public EnemyAction Next { get; protected set; }

    public virtual void Enter() { }

    public virtual void Tick(float deltaTime) { }

    public virtual void Exit() { }

    /// <summary>是否允许被 next 抢占（打断矩阵写在具体动作里）</summary>
    public virtual bool CanBeInterruptedBy(EnemyAction next) => true;
}
