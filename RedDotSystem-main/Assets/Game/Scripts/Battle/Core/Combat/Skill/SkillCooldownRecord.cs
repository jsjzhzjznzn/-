/// <summary>
/// 单个技能的运行时冷却记录（非 MonoBehaviour，纯数据）。
///
/// 一条记录同时覆盖普通冷却与充能型：
///   maxCharges = 1 → charges 只有 0/1，nextRecoverAt 就是冷却结束时刻；
///   maxCharges &gt; 1 → charges 是当前可用次数，nextRecoverAt 是下一层恢复时刻。
///
/// 时刻统一用 <c>Time.time</c>（受 timeScale 影响的游戏时钟）：暂停时停表、顿帧时变慢，
/// 与动画、Buff tick、闪避冷却的口径一致。切换界面/快捷栏不影响这条记录。
/// </summary>
public class SkillCooldownRecord
{
    /// <summary>技能配置（恢复间隔与充能上限都从它读；运行期不复制数值）</summary>
    public SkillDefinition def;

    /// <summary>当前可用充能次数</summary>
    public int charges;

    /// <summary>下一层恢复时刻（Time.time 口径）；满充能时为 0</summary>
    public float nextRecoverAt;

    /// <summary>按配置的最大充能创建记录（初始为满充能）</summary>
    public SkillCooldownRecord(SkillDefinition def)
    {
        this.def = def;
        charges = def != null ? def.maxCharges : 1;
        nextRecoverAt = 0f;
    }

    /// <summary>技能标识哈希（记录键；网络同步也用它）</summary>
    public int skillIdHash => def != null ? def.skillIdHash : 0;
}
