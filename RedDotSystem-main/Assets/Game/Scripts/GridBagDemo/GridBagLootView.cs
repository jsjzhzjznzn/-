using System.Collections.Generic;

using UnityEngine;

namespace GridBagDemo
{
    /// <summary>
    /// 货物清单视图：按配置一次性建好格子（数量固定），物品被拖进背包后该格置空。
    /// </summary>
    public class GridBagLootView : MonoBehaviour
    {
        [SerializeField] private RectTransform slotsRoot;

        [SerializeField] private GridBagLootSlotView slotTemplate;

        private readonly List<GridBagLootSlotView> slots = new();

        /// <summary>清单剩余的物品（下标与格子一一对应，null = 该格已空）</summary>
        private readonly List<GridItemDef> remaining = new();

        public List<GridBagLootSlotView> Slots => slots;

        public GridBagDemo Owner { get; private set; }

        /// <summary>
        /// 建好清单格子并填入物品。
        /// </summary>
        /// <param name="owner">参数名：owner，类型：GridBagDemo，用途：组合根</param>
        /// <param name="defs">参数名：defs，类型：IReadOnlyList&lt;GridItemDef&gt;，用途：清单物品</param>
        public void Build(GridBagDemo owner, IReadOnlyList<GridItemDef> defs)
        {
            Owner = owner;
            slots.Clear();
            remaining.Clear();
            if (slotTemplate == null || slotsRoot == null) return;

            slotTemplate.gameObject.SetActive(false);

            int count = defs?.Count ?? 0;
            for (int i = 0; i < count; i++)
            {
                GridBagLootSlotView slot = Instantiate(slotTemplate, slotsRoot);
                slot.gameObject.name = $"LootSlot_{i}";
                slot.gameObject.SetActive(true);
                slots.Add(slot);
                remaining.Add(null);
            }

            for (int i = 0; i < count; i++) SetSlot(i, defs[i]);
        }

        private void SetSlot(int index, GridItemDef def)
        {
            GridBagLootSlotView slot = slots[index];
            remaining[index] = def;
            slot.Bind(Owner, def, def != null && Owner != null ? Owner.GetIconSprite(def) : null);
        }

        /// <summary>物品被取走：该格置空</summary>
        public void Consume(int index)
        {
            if (index < 0 || index >= slots.Count) return;
            SetSlot(index, null);
        }
    }
}