using System;
using Unity.Netcode;

/// <summary>SkillNetState 的记录种类：技能自身冷却 / 公共冷却分组</summary>
public enum SkillCooldownKind : byte
{
    Skill = 0,
    Group = 1,
}

/// <summary>
/// 技能冷却网络同步状态（NGO NetworkList 的元素类型）。
///
/// 技能记录与公共冷却分组共用一张表：kind 区分，idHash 是 skillIdHash 或 cooldownGroupIdHash。
/// 只传"剩余秒数"而不是绝对时刻：服务端与客户端各自用本地 Time.time（受 timeScale 影响），
/// 暂停一起停、顿帧一起慢，不存在 ServerTime 与缩放时钟混用的问题。
///
/// 约束与 BuffNetState 相同：unmanaged + IEquatable + INetworkSerializeByMemcpy，
/// 相等性必须比较全部字段，否则 NetworkList.Set 的相等门禁会吞掉变更。
/// </summary>
public struct SkillNetState : IEquatable<SkillNetState>, INetworkSerializeByMemcpy
{
    /// <summary>记录种类（<see cref="SkillCooldownKind"/>）</summary>
    public byte kind;

    /// <summary>技能标识哈希或公共冷却组哈希</summary>
    public int idHash;

    /// <summary>当前可用充能次数（分组记录为 0）</summary>
    public int charges;

    /// <summary>同步那一刻的剩余秒数（0 = 已就绪）</summary>
    public float remainSeconds;

    public SkillNetState(byte kind, int idHash, int charges, float remainSeconds)
    {
        this.kind = kind;
        this.idHash = idHash;
        this.charges = charges;
        this.remainSeconds = remainSeconds;
    }

    public bool Equals(SkillNetState other)
        => kind == other.kind
        && idHash == other.idHash
        && charges == other.charges
        && remainSeconds == other.remainSeconds;

    public override bool Equals(object obj) => obj is SkillNetState other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(kind, idHash, charges, remainSeconds);
}
