using UnityEngine;

/// <summary>战斗阵营（友军判定的依据）。Neutral = 未归类，不参与友军判定。</summary>
public enum CombatFaction
{
    Neutral = 0,
    Player = 1,
    Enemy = 2,
}

/// <summary>
/// 阵营归属解析：从命中到的物体（通常是子物体上的 Collider）向上找到"它是谁"。
///
/// 归属规则：
///   Player / AIPlayer  → Player
///   EnemyBrain / ChallengeMonster → Enemy
///   其它（未来的召唤物/衍生生物/环境物件）→ 沿父级继续找；都找不到则 Neutral
///
/// Neutral 不参与友军判定，因此新出现的对象类型默认不会被友军规则误拦。
/// </summary>
public static class CombatFactionUtil
{
    /// <summary>按组件归属解析阵营（沿父级向上）</summary>
    public static CombatFaction Resolve(GameObject go)
    {
        if (go == null) return CombatFaction.Neutral;

        if (go.GetComponentInParent<Player>() != null) return CombatFaction.Player;
        if (go.GetComponentInParent<EnemyBrain>() != null) return CombatFaction.Enemy;
        if (go.GetComponentInParent<ChallengeMonster>() != null) return CombatFaction.Enemy;

        return CombatFaction.Neutral;
    }

    /// <summary>两边是否同一阵营（Neutral 永远不算友军，避免未归类对象互相无法伤害）</summary>
    public static bool IsFriendly(CombatFaction a, CombatFaction b)
    {
        return a != CombatFaction.Neutral && a == b;
    }
}
