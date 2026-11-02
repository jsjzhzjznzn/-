using UnityEngine;

/// <summary>
/// Buff 运行时实例（非 MonoBehaviour，纯数据载体）
/// 由 BuffComponent 在添加 Buff 时创建，持有"这一条 Buff"的全部动态状态：
/// 携带者、施加者、剩余时间、当前层数、tick 计时。
///
/// 静态配置（时长/层数/效果策略引用）在 BuffData 里，实例只引用它，不复制。
/// 效果策略（BuffEffect）是全局共享的，绝不能存运行时状态，全部放这里。
/// </summary>
public class BuffInstance
{
    /// <summary>静态配置（决定效果类型与数值含义）</summary>
    public BuffData data;

    /// <summary>Buff 携带者（效果作用对象；属性效果从它拿 AttributeComponent）</summary>
    public GameObject owner;

    /// <summary>施加来源（最近一次施加者；DoT 结算时作为扣血来源，击杀归属它）</summary>
    public GameObject source;

    /// <summary>当前层数（叠加上限由 data.maxStack 控制）</summary>
    public int stacks;

    /// <summary>剩余时间（秒）；永久 Buff 为极大值</summary>
    public float remainTime;

    /// <summary>tick 计时（距上次 tick 之后累计的秒数；触发后减去间隔而不是清零，避免长期漂移）</summary>
    public float tickTimer;

    /// <summary>退出锁：保证同一实例的效果清理只执行一次（到期/主动移除/死亡清空可能重叠）</summary>
    public bool removed;

    /// <summary>创建一个新实例（duration &lt;= 0 视为永久）</summary>
    public BuffInstance(BuffData data, GameObject owner, GameObject source = null, int stacks = 1)
    {
        this.data = data;
        this.owner = owner;
        this.source = source;
        this.stacks = Mathf.Max(1, stacks);
        remainTime = data != null && data.duration > 0f ? data.duration : float.MaxValue;
    }
}
