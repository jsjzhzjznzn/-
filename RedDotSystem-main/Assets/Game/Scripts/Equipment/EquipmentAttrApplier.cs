using System;
using System.Collections.Generic;

/// <summary>
/// 装备属性应用器：把战斗快照里的装备加成写进 AttributeComponent 的来源账本。
/// 每个实例一个来源对象，卸下/换装/重建都按来源精准撤销——不按数值删（两件都 +30 时会误删）。
/// 全量重建语义：Apply 先清掉本应用器加过的全部来源再逐件重加，重复调用不会叠加。
/// </summary>
public sealed class EquipmentAttrApplier
{
    /// <summary>属性账本（角色根上的 AttributeComponent）</summary>
    private readonly AttributeComponent attribute;

    /// <summary>本应用器已写入的来源（按实例ID一一对应，撤销时逐个 RemoveAllFromSource）</summary>
    private readonly List<EquipmentAttrSource> sources = new();

    /// <summary>来源标识：一件装备一个对象，只用于账本按引用删除</summary>
    private sealed class EquipmentAttrSource
    {
        public long InstanceId;
    }

    public EquipmentAttrApplier(AttributeComponent attribute)
    {
        this.attribute = attribute;
    }

    /// <summary>
    /// 应用一份战斗快照（先撤销上一次应用的来源）。
    /// 快照里的 type 是服务端 AttrType 整数值，未知值跳过（服务端先加枚举时不炸客户端）。
    /// </summary>
    public void Apply(EquipBattleSnapshot snapshot)
    {
        Clear();
        if (attribute == null || snapshot == null || snapshot.items == null) return;

        foreach (EquipBattleItem item in snapshot.items)
        {
            if (item == null || item.attrs == null || item.attrs.Count == 0) continue;

            var source = new EquipmentAttrSource { InstanceId = item.instanceId };
            sources.Add(source);
            foreach (EquipAttrEntry entry in item.attrs)
            {
                if (entry == null || !Enum.IsDefined(typeof(AttrType), entry.type)) continue;
                attribute.Get((AttrType)entry.type)
                    .AddModifier(new AttributeModifier(ModType.Add, entry.value, source));
            }
        }
    }

    /// <summary>撤销本应用器加过的全部加成（角色离场/复活重置时调用）</summary>
    public void Clear()
    {
        if (attribute == null || sources.Count == 0)
        {
            sources.Clear();
            return;
        }

        foreach (AttrType type in Enum.GetValues(typeof(AttrType)))
        {
            MutableAttribute target = attribute.Get(type);
            foreach (EquipmentAttrSource source in sources) target.RemoveAllFromSource(source);
        }
        sources.Clear();
    }
}
