using UnityEngine;

/// <summary>筛选过程统计（供管线汇报"被拦下多少、第一个被拦的是谁"）</summary>
public struct FilterOutcome
{
    /// <summary>被规则/状态拦下的候选数（友军、遮挡、角度）</summary>
    public int blocked;

    /// <summary>第一个被拦下的目标（没有拦截时为 null）</summary>
    public GameObject firstBlocked;

    /// <summary>记录一次拦截</summary>
    public void Block(GameObject target)
    {
        blocked++;
        if (firstBlocked == null) firstBlocked = target;
    }
}

/// <summary>
/// 候选目标查询（管线第 2 步"掩码" + 第 4 步"筛选"）。
///
/// 只做几何与归属层面的收集，不结算伤害 —— 因此粘刀/弹刀/自动索敌这类
/// "只想问一句这里有没有能打的目标"的地方也能复用同一套口径。
///
/// 顺序固定（便宜的先做）：物理层(OverlapSphere) → 排除自身子树 → 有受击组件 →
/// 阵营(友军) → 遮挡 → 角度 → 上限。存活与死亡状态由管线在第 3 步快照里判定。
/// </summary>
public static class CombatQuery
{
    /// <summary>筛选开关（对应原图第 4 步"针对目标做进一步筛选"）</summary>
    public struct Filter
    {
        /// <summary>扇形角（度）：&gt;=360 不做角度筛选（P0 默认 360 = 整球）</summary>
        public float arc;

        /// <summary>是否做遮挡判定</summary>
        public bool requireLineOfSight;

        /// <summary>是否过滤友军</summary>
        public bool filterFriendly;

        /// <summary>攻方阵营</summary>
        public CombatFaction attackerFaction;

        /// <summary>最多产出几个（0 = 不限）</summary>
        public int maxTargets;

        /// <summary>不做任何额外筛选（整球、无遮挡、不过滤友军）</summary>
        public static Filter None => new Filter
        {
            arc = 360f,
            requireLineOfSight = false,
            filterFriendly = false,
            attackerFaction = CombatFaction.Neutral,
            maxTargets = 0,
        };
    }

    /// <summary>
    /// 收集候选目标。结果写入 buffer 并原地压缩，返回有效数量。
    /// </summary>
    /// <param name="attackerRoot">攻方根节点（排除它自身及子物体：武器碰撞体等）</param>
    /// <param name="origin">判定球心</param>
    /// <param name="forward">朝向（角度筛选基准）</param>
    /// <param name="radius">判定半径</param>
    /// <param name="layerMask">物理层</param>
    /// <param name="filter">筛选开关</param>
    /// <param name="buffer">复用的 Collider 缓冲（避免每次打击帧分配）</param>
    /// <param name="outcome">筛选统计（可为默认值）</param>
    public static int Collect(Transform attackerRoot, Vector3 origin, Vector3 forward, float radius,
                              int layerMask, in Filter filter, Collider[] buffer,
                              ref FilterOutcome outcome)
    {
        if (buffer == null || buffer.Length == 0) return 0;

        int count = Physics.OverlapSphereNonAlloc(origin, radius, buffer, layerMask);
        if (count == 0) return 0;

        // 水平化朝向：角色在斜坡/俯仰时 3D 夹角与水平方向不同平面会偏大
        Vector3 flatForward = forward;
        flatForward.y = 0f;
        bool angleCheck = filter.arc < 360f && flatForward.sqrMagnitude > 1e-6f;
        if (angleCheck) flatForward.Normalize();
        float halfArc = filter.arc * 0.5f;

        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            Collider col = buffer[i];
            if (col == null) continue;

            // 排除攻方自身及子物体（全层过滤时会把玩家自己打中）
            if (attackerRoot != null && col.transform.IsChildOf(attackerRoot)) continue;

            var health = col.GetComponentInParent<HealthModel>();
            if (health == null) continue;   // 地形/装饰：没有受击组件，直接跳过（不计入拦截）

            var targetRoot = health.gameObject;

            // 阵营：友军过滤（Neutral 永远不算友军，未归类对象照常可打）
            if (filter.filterFriendly
                && CombatFactionUtil.IsFriendly(filter.attackerFaction, CombatFactionUtil.Resolve(targetRoot)))
            {
                outcome.Block(targetRoot);
                continue;
            }

            // 遮挡：攻方胸口 → 目标胸口，只打 environment 层
            if (filter.requireLineOfSight && !HasLineOfSight(attackerRoot, targetRoot))
            {
                outcome.Block(targetRoot);
                continue;
            }

            // 角度：水平投影后的夹角
            if (angleCheck)
            {
                Vector3 to = targetRoot.transform.position - origin;
                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f && Vector3.Angle(flatForward, to) > halfArc)
                {
                    outcome.Block(targetRoot);
                    continue;
                }
            }

            // 原地压缩（保持物理查询的原始顺序，首个命中目标与改造前一致）
            buffer[valid] = col;
            valid++;

            if (filter.maxTargets > 0 && valid >= filter.maxTargets) break;
        }

        return valid;
    }

    /// <summary>视线：攻方眼高到目标胸口之间没有被 environment 层挡住（与自动索敌同一口径）</summary>
    public static bool HasLineOfSight(Transform attackerRoot, GameObject targetRoot)
    {
        if (attackerRoot == null || targetRoot == null) return false;

        int mask = CombatLayerCache.EnvironmentMask;
        if (mask == 0) return true;   // 没配 environment 层：视作通畅

        Vector3 eye = attackerRoot.position + Vector3.up * 1.4f;
        Vector3 chest = targetRoot.transform.position + Vector3.up * 1.0f;
        return !Physics.Linecast(eye, chest, mask, QueryTriggerInteraction.Ignore);
    }
}
