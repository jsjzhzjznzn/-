using System.Collections.Generic;
using UnityEngine;

/// <summary>一次命中的结果条目（供调用方派发命中事件/播表现）</summary>
public struct HitEntry
{
    /// <summary>受击单位根节点</summary>
    public GameObject target;

    /// <summary>命中的 Collider（取命中点用）</summary>
    public Collider collider;

    /// <summary>是否暴击</summary>
    public bool isCritical;
}

/// <summary>
/// 一次攻击结算的结局（管线返回值）。
/// 调用方据此决定表现：只有 hitCount &gt; 0 才播打击感，
/// 并且整次只播一次（多目标叠加顿帧会让画面卡死）。
/// </summary>
public struct HitReport
{
    /// <summary>实际命中并完成结算的目标数</summary>
    public int hitCount;

    /// <summary>被规则或状态拦下的目标数（已死亡/友军/遮挡/角度）</summary>
    public int blockedCount;

    /// <summary>本次是否有暴击</summary>
    public bool anyCritical;

    /// <summary>第一个被规则/状态拦下的目标（没有拦截时为 null，供"打到但没打进去"的表现使用）</summary>
    public GameObject firstBlocked;

    /// <summary>
    /// 命中明细（按物理查询顺序）。只在本次 Resolve 内有效：
    /// 底层列表是管线复用的，下一次 Resolve 会清空。
    /// </summary>
    public List<HitEntry> hits;

    /// <summary>首个命中目标（打击感基准）</summary>
    public GameObject FirstTarget => hits != null && hits.Count > 0 ? hits[0].target : null;

    /// <summary>首个命中目标的 Collider（打击感取命中点用）</summary>
    public Collider FirstCollider => hits != null && hits.Count > 0 ? hits[0].collider : null;
}

/// <summary>
/// 战斗命中管线（分层结构）：
///
///   ① 收集来源上下文   ← 调用方给 AttackContext
///   ② 规则 + 掩码       ← CombatRunRulesSource（全局）+ CombatQuery.Collect（层/阵营/存活）
///   ③ 目标状态快照      ← TargetSnapshot（存活/眩晕/沉默/无敌，结算前读一次）
///   ④ 进一步筛选        ← 遮挡 → 角度 → 上限（CombatQuery.Filter）
///   ⑤ 结算              ← CalculateOutgoing（或 flatDamage）→ HealthModel.RequestDamage
///
/// 职责边界：本管线只决定"打不打、打谁、打几下"，扣血规则仍在 HealthModel.TakeDamage
/// （无敌窗口拦截/DoT 无视无敌/死亡锁），数值公式仍在 DamageCalculator。三者不互相复制逻辑。
///
/// 运行位置：只能跑在攻方所在端 —— 服务端不参与伤害裁定，出手伤害在攻方算定，
/// 因此这里不做"服务端按服务端状态重算"。
/// </summary>
public static class CombatPipeline
{
    /// <summary>目标状态快照：结算前读一次，结算期间不重读（避免同一帧内状态漂移）</summary>
    private struct TargetSnapshot
    {
        public bool alive;
        public bool invincible;       // 闪避无敌窗口
        public bool buffInvincible;   // Buff 无敌
        public bool stunned;          // 眩晕（Buff 控制）
        public bool silenced;         // 沉默（Buff 控制）
    }

    /// <summary>单次结算的受击去重集合（Resolve 同步执行不重入，一份实例够用，避免每次打击帧分配）</summary>
    private static readonly HashSet<GameObject> HitTargets = new HashSet<GameObject>();

    /// <summary>每次结算复用的命中明细列表（随 HitReport 返回给调用方，下次结算清空）</summary>
    private static readonly List<HitEntry> Entries = new List<HitEntry>(16);

    /// <summary>
    /// 执行一次攻击：判定 → 筛选 → 逐目标结算，返回结局。
    /// </summary>
    /// <param name="ctx">攻击来源上下文</param>
    /// <param name="buffer">复用的 Collider 缓冲（由调用方持有）</param>
    public static HitReport Resolve(in AttackContext ctx, Collider[] buffer)
    {
        var report = default(HitReport);
        if (buffer == null || buffer.Length == 0) return report;

        // ② 全局规则：友军伤害是否放开；招式要求的遮挡是否被全局否决
        CombatRunRules rules = CombatRunRulesSource.Current;

        var attacker = ctx.attacker;
        Transform attackerRoot = attacker != null ? attacker.transform : null;

        var filter = new CombatQuery.Filter
        {
            arc = ctx.arc,
            requireLineOfSight = ctx.requireLineOfSight && rules.lineOfSightEnabled,
            filterFriendly = !rules.friendlyFire,
            attackerFaction = ctx.faction,
            maxTargets = ctx.maxTargets,
        };

        var outcome = new FilterOutcome();
        int count = CombatQuery.Collect(attackerRoot, ctx.origin, ctx.forward, ctx.radius,
                                        ctx.layerMask, filter, buffer, ref outcome);
        report.blockedCount += outcome.blocked;
        report.firstBlocked = outcome.firstBlocked;

        Entries.Clear();
        HitTargets.Clear();
        report.hits = Entries;

        try
        {
            for (int i = 0; i < count; i++)
            {
                Collider col = buffer[i];
                if (col == null) continue;

                var health = col.GetComponentInParent<HealthModel>();
                if (health == null) continue;

                var targetRoot = health.gameObject;

                // ③ 目标状态快照：结算前读一次（眩晕/沉默/无敌供扩展规则使用）
                var snapshot = Snapshot(health);

                // 死亡目标不接受伤害（与改造前一致：活体判定 + 角色死亡标记）
                if (!snapshot.alive || IsPlayerDead(health))
                {
                    report.blockedCount++;
                    if (report.firstBlocked == null) report.firstBlocked = targetRoot;
                    continue;
                }

                if (!HitTargets.Add(targetRoot)) continue;   // 同一受击单位多 Collider 只结算一次

                // ⑤ 结算：出手伤害在攻方这一刻算定，之后攻方属性变化不回溯。
                // 无敌窗口的拦下与 DoT 例外仍由 HealthModel.TakeDamage 负责（管线不复制扣血规则）。
                var outgoing = CalculateOutgoing(ctx, attacker);
                health.RequestDamage(new DamageRequest
                {
                    damage = outgoing.damage,
                    isCritical = outgoing.isCritical,
                    sourceId = ctx.sourceId,
                    isDoT = false,
                }, attacker);

                // 命中附带 Buff：本次伤害结算完再挂（挂上的加成从下一击生效）
                if (ctx.hitBuff != null)
                    targetRoot.GetComponent<BuffComponent>()?.RequestAddBuff(ctx.hitBuff, attacker);

                report.hitCount++;
                if (outgoing.isCritical) report.anyCritical = true;

                Entries.Add(new HitEntry
                {
                    target = targetRoot,
                    collider = col,
                    isCritical = outgoing.isCritical,
                });
            }
        }
        finally
        {
            HitTargets.Clear();
        }

        return report;
    }

    /// <summary>只做判定不结算：给粘刀/弹刀/自动索敌这类"这里有没有能打的人"的问题复用同一口径</summary>
    public static int Query(in AttackContext ctx, in CombatQuery.Filter filter, Collider[] buffer,
                            ref FilterOutcome outcome)
    {
        Transform attackerRoot = ctx.attacker != null ? ctx.attacker.transform : null;
        return CombatQuery.Collect(attackerRoot, ctx.origin, ctx.forward, ctx.radius,
                                   ctx.layerMask, filter, buffer, ref outcome);
    }

    // ==================== 内部 ====================

    /// <summary>出手伤害（含暴击掷骰）：flatDamage 优先，否则读攻方属性</summary>
    private static (float damage, bool isCritical) CalculateOutgoing(in AttackContext ctx, GameObject attacker)
    {
        if (ctx.flatDamage > 0f) return (ctx.flatDamage, false);

        var result = DamageCalculator.CalculateOutgoing(
            attacker, ctx.damageMultiplier, ctx.critRate, ctx.critMultiplier);
        return (result.finalDamage, result.isCritical);
    }

    /// <summary>拍目标状态快照（每样只读一次）</summary>
    private static TargetSnapshot Snapshot(HealthModel health)
    {
        var snapshot = new TargetSnapshot
        {
            alive = health.IsAlive,
            invincible = health.IsInvincible,
        };

        var state = health.GetComponent<CharacterState>();
        if (state != null)
        {
            snapshot.buffInvincible = state.IsInvincible;
            snapshot.stunned = state.IsStun;
            snapshot.silenced = state.IsSilence;
        }

        return snapshot;
    }

    /// <summary>角色死亡标记（HealthModel 已判活，这里补 Player 自己的死亡状态）</summary>
    private static bool IsPlayerDead(HealthModel health)
    {
        var player = health.GetComponent<Player>();
        return player != null && player.IsDead;
    }
}
