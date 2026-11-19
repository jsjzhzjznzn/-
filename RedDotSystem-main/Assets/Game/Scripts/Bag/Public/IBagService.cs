namespace Game.Bag
{
    using System.Collections.Generic;

    using Common.Network;

    using MieMieFrameWork.Business;

    /// <summary>
    /// 背包模块公共服务：只读查询 + 命令
    /// 服务端权威，本接口不做任何规则判断
    /// </summary>
    public interface IBagService : IGameService
    {
        /// <summary>武器区容量（服务端下发；未加载过为 0）</summary>
        int Capacity { get; }

        /// <summary>道具区当前容量（按快照最高槽位推导）</summary>
        int ItemCapacity { get; }

        /// <summary>槽位总数（武器区 + 道具区）</summary>
        int SlotCount { get; }

        /// <summary>是否正在等待列表响应</summary>
        bool IsLoading { get; }

        /// <summary>武器区已用格数</summary>
        int UsedWeaponCount { get; }

        /// <summary>道具区已用格数</summary>
        int UsedItemCount { get; }

        /// <summary>物品配置（按 itemId 升序）</summary>
        IReadOnlyList<ItemConfigInfo> AllConfigs { get; }

        /// <summary>该槽位是否属于武器区（武器区 [0, Capacity)）</summary>
        bool IsWeaponSlot(int index);

        /// <summary>取槽位；越界或空槽返回 null</summary>
        BagSlotInfo GetSlot(int index);

        /// <summary>取物品配置；未加载到返回 null</summary>
        ItemConfigInfo GetConfig(int itemId);

        /// <summary>某槽位是否有物品</summary>
        bool HasItem(int index);

        /// <summary>按物品ID统计持有总量</summary>
        int GetItemCount(int itemId);

        /// <summary>请求背包列表（物品配置表随本次响应一起下发）</summary>
        void RequestBag();

        /// <summary>使用物品（校验与扣减在服务端）</summary>
        void UseItem(int slotIndex, int count = 1);

        /// <summary>丢弃物品</summary>
        void DiscardItem(int slotIndex, int count = 1);

        /// <summary>整理背包（同类合并 + 排序，规则在服务端）</summary>
        void Tidy();

        /// <summary>槽位换位</summary>
        void SwapSlots(int fromSlot, int toSlot);

        /// <summary>发放物品（测试发放，本期物品唯一来源）</summary>
        void Grant(int itemId, int count);
    }
}
