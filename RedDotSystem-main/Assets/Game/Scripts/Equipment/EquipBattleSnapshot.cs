using System;
using System.Collections.Generic;

/// <summary>
/// 装备加成的一条属性（对应服务端 equipment_config.attr_json 的一项）
/// type 是 AttrType 的整数值（服务端配置写入，枚举只允许末尾追加）
/// </summary>
[Serializable]
public class EquipAttrEntry
{
    public int type;
    public float value;
}

/// <summary>attr_json 的包装（JsonUtility 不支持顶层数组，用一个对象包住）</summary>
[Serializable]
public class EquipAttrList
{
    public List<EquipAttrEntry> items = new();
}

/// <summary>
/// 战斗用装备快照（单机直接本地生成，联机由各客户端把自己的快照发给房主）
/// 只带"这一件装备带来的加成"，属性应用方按 instanceId 作为来源标识增删，重复应用不会叠加
/// </summary>
[Serializable]
public class EquipBattleSnapshot
{
    /// <summary>角色ID（-1 = 无角色）</summary>
    public int charId = -1;

    /// <summary>已穿戴的装备（本期只有武器槽一件）</summary>
    public List<EquipBattleItem> items = new();
}

/// <summary>快照里的一件装备</summary>
[Serializable]
public class EquipBattleItem
{
    /// <summary>实例ID（同时作为属性 Modifier 的来源标识）</summary>
    public long instanceId;

    /// <summary>物品ID</summary>
    public int itemId;

    /// <summary>强化等级（本期恒 0）</summary>
    public int enhanceLevel;

    /// <summary>这件装备提供的属性加成</summary>
    public List<EquipAttrEntry> attrs = new();
}
