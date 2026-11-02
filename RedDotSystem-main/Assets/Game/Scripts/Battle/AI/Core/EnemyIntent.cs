using UnityEngine;

/// <summary>
/// 决策层 → 动作层的唯一通信协议（Intent）。
///
/// 为什么要有它：
/// 行为树的 tick 是 0.15~0.25s 级，而动画判定帧要求 ±1 帧精度；如果让树直接 Play 动画，
/// 会出现"树重入导致重播起手""受击了还在挥""Running 节点抢不回来"三类故障。
/// 所以树只发意图，动作层决定"接不接受/什么时候结束"，两边通过回执解耦。
/// </summary>
public enum EnemyIntentType
{
    Idle = 0,
    MoveTo = 1,
    Strafe = 2,
    FaceTarget = 3,
    Attack = 4,
    Backstep = 5,
}

/// <summary>一次意图请求（结构体：决策层零分配地发请求）。</summary>
public struct EnemyIntent
{
    public EnemyIntentType Type;

    /// <summary>招式 id（Type=Attack 时使用，对应 EnemyProfile.attacks[].id）</summary>
    public string AttackId;

    /// <summary>环绕方向：+1 / -1（Type=Strafe 时使用，0 表示随机）</summary>
    public int Direction;

    public static EnemyIntent Idle() => new EnemyIntent { Type = EnemyIntentType.Idle };

    public static EnemyIntent MoveTo() => new EnemyIntent { Type = EnemyIntentType.MoveTo };

    public static EnemyIntent Strafe(int direction) => new EnemyIntent
    {
        Type = EnemyIntentType.Strafe,
        Direction = direction,
    };

    public static EnemyIntent FaceTarget() => new EnemyIntent { Type = EnemyIntentType.FaceTarget };

    public static EnemyIntent Attack(string attackId) => new EnemyIntent
    {
        Type = EnemyIntentType.Attack,
        AttackId = attackId,
    };

    public static EnemyIntent Backstep() => new EnemyIntent { Type = EnemyIntentType.Backstep };
}

/// <summary>意图被接受/拒绝的结果（树用它决定下一 tick 的分支）。</summary>
public struct EnemyIntentResult
{
    public bool Accepted;

    /// <summary>被接受时是不是"需要等动作结束"的阻塞动作（攻击/受击/后撤）</summary>
    public bool BlocksUntilFinished;

    public static EnemyIntentResult Reject() => new EnemyIntentResult { Accepted = false };
    public static EnemyIntentResult Accept(bool blocks) => new EnemyIntentResult { Accepted = true, BlocksUntilFinished = blocks };
}
