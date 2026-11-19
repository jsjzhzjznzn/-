using UnityEngine;

/// <summary>
/// 攻击来源类型：同一次伤害来自哪种投递方式，供管线决定"要不要做几何筛选"。
/// DoT 没有判定几何，不进管线，只在这里登记语义。
/// </summary>
public enum AttackDelivery
{
    /// <summary>近战挥砍（玩家连招）</summary>
    Melee = 0,

    /// <summary>技能（玩家 SkillATK，与近战共用判定，只是数据来源不同）</summary>
    Skill = 1,

    /// <summary>敌方出手（BOSS 行为树招式）</summary>
    EnemyAttack = 2,

    /// <summary>脚本直接结算（挑战怪物/测试木桩：无判定几何，只打指定目标）</summary>
    Scripted = 3,

    /// <summary>持续伤害（Buff tick）：不经过管线</summary>
    Dot = 4,
}

/// <summary>
/// 一次攻击的来源上下文（管线第 1 步的产物）。
///
/// 由各个攻击来源自己填：玩家连招从 ComboData 拼，BOSS 从 AttackDef 拼，
/// 挑战怪物/测试木桩用"极小半径整球 + 单目标"表达"只打指定目标"。
/// 管线只读它，不回写。
///
/// 伤害来源二选一：
///   flatDamage &gt; 0  → 直接用 flatDamage 作为出手伤害（BOSS/怪物/木桩这类不读攻方属性的来源）
///   否则走 DamageCalculator.CalculateOutgoing（攻击力 × damageMultiplier × 增伤 × 暴击）
/// </summary>
public struct AttackContext
{
    /// <summary>出手者（飘字来源、命中事件、打击感、命中附带 Buff 的施加者）</summary>
    public GameObject attacker;

    /// <summary>阵营（P1 友军过滤用；由管线在 attacker 为空时按归属解析）</summary>
    public CombatFaction faction;

    /// <summary>判定球心（已含招式前移量）</summary>
    public Vector3 origin;

    /// <summary>朝向（角度筛选基准；传入时会被水平化）</summary>
    public Vector3 forward;

    /// <summary>判定半径（米）</summary>
    public float radius;

    /// <summary>扇形角（度）：360 = 整球不做角度筛选；100 = 左右各 50°</summary>
    public float arc;

    /// <summary>物理层过滤（攻击方可打到的层）</summary>
    public int layerMask;

    /// <summary>来源类型</summary>
    public AttackDelivery delivery;

    /// <summary>是否要求视线遮挡判定</summary>
    public bool requireLineOfSight;

    /// <summary>本击最多命中几个目标（0 = 不限）</summary>
    public int maxTargets;

    /// <summary>命中附带 Buff（可空）</summary>
    public BuffData hitBuff;

    // ============ 数值来源（flatDamage 与 damageMultiplier 二选一） ============

    /// <summary>固定出手伤害（&gt;0 时使用；已含攻方的全部加成）</summary>
    public float flatDamage;

    /// <summary>招式伤害倍率（走攻方属性结算时使用）</summary>
    public float damageMultiplier;

    /// <summary>暴击率（0~1，每个目标独立掷骰）</summary>
    public float critRate;

    /// <summary>暴击倍率</summary>
    public float critMultiplier;

    /// <summary>联网用的攻击者 NetworkObjectId（未 spawn 时为 0）</summary>
    public ulong sourceId;
}
