using UnityEngine;

/// <summary>
/// 特效注册键集中定义（照 RedDotKeys 的先例：字符串键统一管理，调用方不写魔法字符串）
/// 对应注册表资产：Assets/Game/Resources/VfxCatalog.asset
/// </summary>
public static class VfxKeys
{
    /// <summary>玩家挥击弧光（左键攻击时在玩家前方播放）</summary>
    public const string PlayerAttackSlash = "player_attack_slash";

    /// <summary>敌人受击爆闪（命中敌人时在敌人身上播放）</summary>
    public const string EnemyHit = "enemy_hit";

    /// <summary>敌人死亡星星消散（怪物缩放消散时播放）</summary>
    public const string EnemyDie = "enemy_die";

    /// <summary>击杀直接入包金币飞溅（A型·拾金鼠死亡时播放；循环型特效，靠时长覆盖回收）</summary>
    public const string KillCoins = "kill_coins";

    /// <summary>拾取成功星光迸发（B型·藏宝龟掉落物入包时播放）</summary>
    public const string PickupBurst = "pickup_burst";

    /// <summary>弹刀火花（黄色迸发；与拾取共用同一 prefab，但独立注册便于单独调池/缩放）</summary>
    public const string DeflectSpark = "deflect_spark";
}
