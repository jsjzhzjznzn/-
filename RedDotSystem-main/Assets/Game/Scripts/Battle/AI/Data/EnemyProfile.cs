using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敌人档案（可配置资产的唯一入口）：移动、感知、防御、招式表、表现片段。
///
/// 为什么用 SO：策划改数值不用动场景/预制体，也不会在预制体上堆一屏覆盖字段；
/// 副本小怪与 BOSS 共用同一套代码，只是换一份档案。
/// </summary>
[CreateAssetMenu(menuName = "Battle/AI/Enemy Profile", fileName = "EnemyProfile")]
public class EnemyProfile : ScriptableObject
{
    [Header("移动")]
    public float moveSpeed = 1.6f;

    /// <summary>转向速度（度/秒）</summary>
    public float turnSpeedDeg = 240f;

    [Header("交战距离")]
    /// <summary>进入这个距离才考虑出手</summary>
    public float attackRange = 2.3f;

    /// <summary>追击停止距离（略小于 attackRange，形成滞回不来回抽搐）</summary>
    public float stopDistance = 1.9f;

    [Header("感知")]
    public float sightRange = 25f;
    [Range(30f, 360f)] public float sightAngle = 160f;
    public float loseTargetSeconds = 5f;

    /// <summary>发现目标后的反应延迟（秒）：120~250ms 最像人</summary>
    [Range(0f, 1f)] public float reactionDelay = 0.18f;

    [Header("防御 / 韧性")]
    public float maxPoise = 60f;

    /// <summary>每点伤害折算多少削韧（1.0 = 伤害即削韧；越大越容易被破防）</summary>
    public float poisePerDamage = 1.2f;

    /// <summary>最后一次受伤后多久开始回韧</summary>
    public float poiseRecoverDelay = 2f;

    /// <summary>回满韧性所需时间（秒）</summary>
    public float poiseRecoverSeconds = 3f;

    /// <summary>破防硬直时长</summary>
    public float staggerSeconds = 0.9f;

    /// <summary>普通受击硬直时长（只在后摇/空闲时播，不打断前摇）</summary>
    public float hurtStaggerSeconds = 0.4f;

    /// <summary>单次伤害达到这个值视为"重击"（重击仍需打空韧性才打断前摇）</summary>
    public float heavyHitDamage = 15f;

    [Header("受击打断")]
    [Tooltip("轻击打断前摇的概率：0 = 前摇霸体（BOSS 能完整出招），1 = 每刀都打断。重击/破韧始终打断")]
    [Range(0f, 1f)] public float lightHitInterruptChance = 0.3f;

    [Header("受击状态")]
    [Tooltip("轻击受击片段（短片段，如 Monster_Goblin_Ani_Hit_Shake）：每刀都切受击，连打只延长时长、不重播片段")]
    public AnimationClip flinchClip;

    [Tooltip("（保留）上半身 AvatarMask：当前方案不分层，留着以后做叠加受击用")]
    public AvatarMask flinchMask;

    [Tooltip("轻击受击时长（秒）：要略大于玩家出刀间隔，连打时怪才会一直挂在受击里")]
    public float flinchSeconds = 0.6f;

    [Tooltip("重击受击片段（正面命中）")]
    public AnimationClip heavyHitFrontClip;

    [Tooltip("重击受击片段（背面命中）")]
    public AnimationClip heavyHitBackClip;

    [Tooltip("重击受击硬直时长（秒）")]
    public float heavyHitSeconds = 0.9f;

    [Tooltip("重击受击片段播放倍速；<=0 = 自动按片段长度/硬直时长计算（正背面片段长度不同也一次播完）")]
    public float heavyHitSpeed = -1f;

    [Header("阶段演出")]
    [Tooltip("阶段切换时的演出片段（吼叫/爆气）；留空则只算时长不播动画")]
    public AnimationClip phaseIntroClip;
    [Tooltip("阶段演出时长（秒）：期间不可被打断，但伤害照吃")]
    public float phaseIntroSeconds = 0.8f;

    [Tooltip("每进一个阶段，攻速/出手频率提升的比例（0.1 = +10%）")]
    [Range(0f, 0.5f)]
    public float phaseAttackSpeedBonus = 0.1f;

    [Header("待机破绽（§8 P2 #15）")]
    [Tooltip("挑衅/喘息片段；留空则只算时长不播动画")]
    public AnimationClip tauntClip;
    [Tooltip("挑衅持续时长（秒）：期间无霸体，任何攻击都能打断——这就是给玩家的进攻窗口")]
    public float tauntSeconds = 1.2f;
    [Tooltip("两次挑衅的最小/最大间隔（秒），取随机值避免节奏固定")]
    public float tauntIntervalMin = 15f;
    public float tauntIntervalMax = 25f;

    [Tooltip("目标距离超过 攻击范围 × 该倍数 才允许挑衅：贴身缠斗时不再插入站桩")]
    public float tauntMinDistanceFactor = 1.5f;

    [Header("节奏")]
    /// <summary>两次出手之间的全局间隔（秒，会乘以下面的抖动）</summary>
    public float globalCooldown = 0.6f;
    [Range(0.5f, 1f)] public float intervalJitterMin = 0.85f;
    [Range(1f, 1.5f)] public float intervalJitterMax = 1.15f;

    [Header("走位倾向")]
    [Range(0f, 1f)] public float strafeChance = 0.45f;
    public float strafeDuration = 0.9f;
    public float strafeRadius = 2.6f;
    [Range(0f, 1f)] public float backstepChance = 0.35f;
    public float backstepDistance = 2f;

    [Header("招式表")]
    public AttackDef[] attacks = new AttackDef[0];

    [Header("表现片段")]
    public AnimationClip idleClip;
    public AnimationClip runClip;
    public AnimationClip hurtClip;

    [Header("走位片段（消除侧移脚滑）")]
    [Tooltip("左/右/前/后的行走片段：侧移时按移动方向选对应片段，缺哪个就回退 runClip")]
    public AnimationClip walkLeftClip;
    public AnimationClip walkRightClip;
    public AnimationClip walkForwardClip;
    public AnimationClip walkBackClip;

    [Tooltip("走位片段对应的作者设计移速（m/s）：动作层按 实际移速 / 该值 校正播放速度")]
    public float walkClipSpeed = 1f;

    /// <summary>破防硬直片段（没有则回退 hurtClip）</summary>
    public AnimationClip staggerClip;

    /// <summary>后撤步片段（没有则用 runClip 倒放替代）</summary>
    public AnimationClip dodgeClip;

    public AnimationClip deathClip;

    [Header("阶段")]
    /// <summary>血量百分比阈值（100 = 满血即阶段 1；{100, 50} 表示 50% 进阶段 2）</summary>
    public int[] phaseHpPercents = { 100 };

    // ──────────────────────────────
    // 查询
    // ──────────────────────────────

    /// <summary>按 id 找招式（找不到返回 null）</summary>
    public AttackDef FindAttack(string id)
    {
        if (string.IsNullOrEmpty(id) || attacks == null) return null;
        for (int i = 0; i < attacks.Length; i++)
        {
            if (attacks[i] != null && attacks[i].id == id) return attacks[i];
        }
        return null;
    }

    /// <summary>当前阶段（从 1 开始，按 hpPercent 倒序阈值判定）</summary>
    public int PhaseFor(float hpPercent)
    {
        if (phaseHpPercents == null || phaseHpPercents.Length == 0) return 1;
        int phase = 1;
        for (int i = 0; i < phaseHpPercents.Length; i++)
        {
            if (hpPercent <= phaseHpPercents[i]) phase = i + 1;
        }
        return Mathf.Max(1, phase);
    }

    /// <summary>
    /// 按"距离可用 + 阶段可用 + 冷却就绪 + 权重"挑一招；尽量不与上一招重复。
    /// 返回 null = 当前没有可用招式（决策层应改为走位）。
    /// </summary>
    public AttackDef PickAttack(float distance, int phase, Dictionary<string, float> cooldowns, string lastAttackId, out float totalWeight)
    {
        totalWeight = 0f;
        if (attacks == null || attacks.Length == 0) return null;

        // 先按硬条件过滤，再按权重随机；过滤后只剩上一招时允许重复（总比不出手好）
        float weightSum = 0f;
        AttackDef fallback = null;
        float fallbackWeight = 0f;

        for (int i = 0; i < attacks.Length; i++)
        {
            AttackDef def = attacks[i];
            if (def == null || def.clip == null) continue;
            if (def.minPhase > phase) continue;

            // 用"有效射程"做宽松判定：略超出半径仍可起手（前冲会补上距离）
            float reach = def.range + Mathf.Max(0f, def.advanceDistance) + 0.4f;
            if (distance > reach) continue;

            if (cooldowns != null && cooldowns.TryGetValue(def.id, out float readyAt) && Time.time < readyAt) continue;

            fallbackWeight += def.weight;
            if (def.id == lastAttackId)
            {
                fallback = def;   // 只作为兜底
                continue;
            }

            weightSum += def.weight;
        }

        totalWeight = weightSum;
        if (weightSum <= 0.0001f)
        {
            totalWeight = fallbackWeight;
            return fallbackWeight > 0.0001f ? fallback : null;
        }

        float roll = Random.value * weightSum;
        float acc = 0f;
        for (int i = 0; i < attacks.Length; i++)
        {
            AttackDef def = attacks[i];
            if (def == null || def.clip == null) continue;
            if (def.minPhase > phase) continue;
            if (def.id == lastAttackId) continue;

            float reach = def.range + Mathf.Max(0f, def.advanceDistance) + 0.4f;
            if (distance > reach) continue;
            if (cooldowns != null && cooldowns.TryGetValue(def.id, out float readyAt) && Time.time < readyAt) continue;

            acc += def.weight;
            if (roll <= acc) return def;
        }
        return fallback;
    }

    private void OnValidate()
    {
        moveSpeed = Mathf.Max(0.1f, moveSpeed);
        attackRange = Mathf.Max(0.5f, attackRange);
        stopDistance = Mathf.Clamp(stopDistance, 0.3f, attackRange);
        maxPoise = Mathf.Max(1f, maxPoise);
        staggerSeconds = Mathf.Max(0.1f, staggerSeconds);

        if (attacks != null)
        {
            for (int i = 0; i < attacks.Length; i++) attacks[i]?.Validate();
        }
    }
}
