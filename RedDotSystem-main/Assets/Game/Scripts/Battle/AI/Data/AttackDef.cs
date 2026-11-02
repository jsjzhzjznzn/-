using UnityEngine;

/// <summary>
/// 单招定义（数据驱动）：一段动画 + 判定窗口 + 数值 + 选招权重。
///
/// 判定帧用**归一化时间（0~1）**，不是秒——项目里踩过"传秒数导致事件永不触发"的坑，
/// 而且用归一化时间后，招式加速/减速时判定帧自动跟着动画走。
/// </summary>
[System.Serializable]
public class AttackDef
{
    [Header("身份")]
    public string id = "attack";

    [Header("动画")]
    public AnimationClip clip;

    /// <summary>切入淡入时长（秒）：攻击建议 0.06~0.1，太长会"软绵绵"</summary>
    [Range(0f, 0.5f)] public float fadeIn = 0.08f;

    /// <summary>播放速度倍率（>1 更快，判定帧按动画时间自动同步）</summary>
    [Range(0.4f, 2.5f)] public float speed = 1f;

    [Header("窗口（归一化 0~1）")]
    /// <summary>命中帧（可多段）。0.35 = 动画走到 35% 时结算</summary>
    public float[] hitFrames = { 0.35f };

    /// <summary>后摇起点：之后允许被玩家打断（抓后摇的惩罚窗口）</summary>
    [Range(0f, 1f)] public float recoveryStart = 0.7f;

    [Header("片段裁剪（归一化 0~1）")]
    /// <summary>从片段的哪里开始播（0 = 从头）。用于跳过片段自带的静止帧。</summary>
    [Range(0f, 0.9f)] public float startNormalized = 0f;

    /// <summary>
    /// 播到片段的哪里结束（1 = 到结尾）。砍掉尾部收招/回正——实战里那一段只是等待，
    /// 玩家早就在等下一招。结束时用下一动作的淡入时间交叉淡出，不会硬切。
    /// </summary>
    [Range(0.1f, 1f)] public float endNormalized = 1f;

    [Header("连段")]
    /// <summary>本招可以续接的下一招（进入时按概率掷选，最多续接 3 段）</summary>
    public ComboLink[] followUps = new ComboLink[0];

    [Header("数值")]
    public float damage = 10f;

    /// <summary>削韧值：累计超过 profile.maxPoise 就破防硬直</summary>
    public float poiseDamage = 18f;

    [Header("判定几何")]
    /// <summary>判定半径（米）</summary>
    public float range = 2.2f;

    /// <summary>扇形角（度）：100 = 左右各 50°，闪到侧面就能躲开</summary>
    [Range(20f, 360f)] public float arc = 100f;

    /// <summary>前冲距离（米）：起手向目标方向压上</summary>
    public float advanceDistance = 0.8f;

    /// <summary>前冲在"动作前段"的占比（0.3 = 前 30% 时间内冲完）</summary>
    [Range(0.05f, 0.9f)] public float advanceEnd = 0.3f;

    [Header("节奏与选招")]
    /// <summary>本招冷却（秒）</summary>
    public float cooldown = 1.8f;

    /// <summary>选招权重（越大越常用）</summary>
    public float weight = 1f;

    /// <summary>最低阶段：1 起（阶段 2 才解锁的招式填 2）</summary>
    public int minPhase = 1;

    [Header("表现")]
    /// <summary>起手特效键（VfxCatalog 的键；空则不放）</summary>
    public string telegraphVfxKey = "";

    /// <summary>动作前段时长（秒），供"预兆"逻辑/调试读取</summary>
    public float StartupSeconds(EnemyProfile profile)
    {
        if (clip == null) return 0f;
        float firstHit = Mathf.Clamp01(hitFrames != null && hitFrames.Length > 0 ? hitFrames[0] : 0.35f);
        float lead = Mathf.Max(0f, firstHit - startNormalized);
        return clip.length * lead / Mathf.Max(0.05f, speed);
    }

    /// <summary>校正非法配置（编辑器与运行时都调用，避免 0/负值把判定吃掉）</summary>
    public void Validate()
    {
        if (string.IsNullOrEmpty(id)) id = "attack";
        fadeIn = Mathf.Clamp(fadeIn, 0f, 0.5f);
        speed = Mathf.Clamp(speed, 0.4f, 2.5f);
        recoveryStart = Mathf.Clamp01(recoveryStart);
        range = Mathf.Max(0.3f, range);
        arc = Mathf.Clamp(arc, 20f, 360f);
        advanceDistance = Mathf.Max(0f, advanceDistance);
        advanceEnd = Mathf.Clamp(advanceEnd, 0.05f, 0.9f);
        cooldown = Mathf.Max(0.1f, cooldown);
        weight = Mathf.Max(0.01f, weight);
        minPhase = Mathf.Max(1, minPhase);

        if (hitFrames == null || hitFrames.Length == 0) hitFrames = new[] { 0.35f };
        for (int i = 0; i < hitFrames.Length; i++) hitFrames[i] = Mathf.Clamp01(hitFrames[i]);

        startNormalized = Mathf.Clamp(startNormalized, 0f, 0.9f);
        endNormalized = Mathf.Clamp(endNormalized, 0.1f, 1f);
        if (endNormalized - startNormalized < 0.1f) endNormalized = Mathf.Min(1f, startNormalized + 0.1f);

        if (followUps != null)
        {
            for (int i = 0; i < followUps.Length; i++) followUps[i]?.Validate();
        }
    }

    /// <summary>裁剪窗口占整段片段的比例（1 = 整段）</summary>
    public float TrimmedSpan => Mathf.Clamp(endNormalized - startNormalized, 0.1f, 1f);

    /// <summary>裁剪后的实际播出时长（秒），含速度倍率</summary>
    public float TrimmedDuration => clip == null ? 0f : clip.length * TrimmedSpan / Mathf.Max(0.05f, speed);

    /// <summary>连段链路：本招在某个进度点直接取消后摇、续接下一招</summary>
    [System.Serializable]
    public class ComboLink
    {
        /// <summary>续接目标招式的 id</summary>
        public string attackId;

        /// <summary>在动作进度到达此处时换招（0~1，相对裁剪后的动作）</summary>
        [Range(0.3f, 1f)] public float atNormalized = 0.85f;

        /// <summary>触发概率（0 = 不会接，1 = 必接）</summary>
        [Range(0f, 1f)] public float chance = 0.6f;

        public void Validate()
        {
            atNormalized = Mathf.Clamp(atNormalized, 0.3f, 1f);
            chance = Mathf.Clamp01(chance);
        }
    }
}
