using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 角色数据资产（Inspector 右键 Create/Character/Player 创建）
/// 一个角色一份配置，集中管理所有移动状态数据
/// </summary>
[CreateAssetMenu(fileName = "Player", menuName = "Create/Character/Player")]
public class PlayerSO : ScriptableObject
{
    [field: SerializeField] public PlayerMovementData movementData { get; private set; }

    // 预留：连击系统接入后使用
    [field: SerializeField] public PlayerComboData comboData { get; private set; }

    /// <summary>基础属性（攻击力 / 防御力 / 最大血量）：角色生成时写入 AttributeComponent / HealthModel</summary>
    [field: SerializeField, Header("基础属性（攻击力 / 防御力 / 最大血量）")]
    public CharacterStatsData statsData { get; private set; }

    /// <summary>
    /// 技能槽：下标 0 = 技能键，后续 Q/E/R 等按槽位扩展。
    /// 为空时技能键不产生任何行为（不进冷却、不播动画）。
    /// </summary>
    [field: SerializeField, Header("技能槽（下标 0 = 技能键）")]
    public List<SkillDefinition> skillSlots { get; private set; } = new List<SkillDefinition>();

    /// <summary>
    /// 英雄编号（与 BattleHeroCatalog / 服务端 char_id 对齐：安比=0、艾莲=1；-1 = 未登记）。
    /// 装备按 char_id 归属，单机场景靠它识别"这套属性属于哪个角色"；联机以 RoomState.CharId 为准。
    /// 用普通字段而不是 [field: SerializeField] 自动属性：序列化名是属性背后的编译器字段，
    /// 工具/编辑器脚本按名字读写会找不到（这里要在构建器里按名匹配）。
    /// </summary>
    [SerializeField, Header("英雄编号（0 安比 / 1 艾莲，-1 未登记）")]
    private int characterId = -1;

    /// <summary>英雄编号的只读访问器（运行时用）</summary>
    public int CharacterId => characterId;
}
