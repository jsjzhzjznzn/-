namespace Game.Equipment
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 装备模块公共服务：只读查询 + 命令
    /// 服务端权威，穿脱与属性都由服务端配置与回执驱动
    /// </summary>
    public interface IEquipmentService : IGameService
    {
        /// <summary>是否已经拿到过装备数据</summary>
        bool IsLoaded { get; }

        /// <summary>是否正在请求</summary>
        bool IsLoading { get; }

        /// <summary>取装备配置；未加载返回 null</summary>
        EquipConfigInfo GetConfig(int itemId);

        /// <summary>取装备实例；不存在返回 null</summary>
        EquipInstanceInfo GetInstance(long instanceId);

        /// <summary>取某角色某槽位当前装备；空槽返回 null</summary>
        EquipInstanceInfo GetEquipped(int charId, int slotType);

        /// <summary>某件武器当前被哪个角色装备（0 = 未装备）</summary>
        int GetEquippedChar(long instanceId);

        /// <summary>全部武器实例</summary>
        List<EquipInstanceInfo> GetWeaponInstances();

        /// <summary>某件装备提供的属性条目</summary>
        List<EquipAttrEntry> GetAttrEntries(int itemId);

        /// <summary>角色装备属性加成合计</summary>
        EquipAttrTotals GetEquipAttrBonus(int charId, long candidateInstanceId = 0);

        /// <summary>某件装备的某类属性数值</summary>
        float GetAttrValue(int itemId, AttrType type);

        /// <summary>联机同步用的战斗属性快照</summary>
        EquipBattleSnapshot BuildBattleSnapshot(int charId);

        /// <summary>未加载时请求一次</summary>
        void EnsureLoaded();

        /// <summary>请求装备列表</summary>
        void RequestEquipment();

        /// <summary>装备</summary>
        void Equip(int charId, int slotType, long instanceId);

        /// <summary>卸下</summary>
        void Unequip(int charId, int slotType);
    }
}
