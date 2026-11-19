using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GridBagDemo
{
    /// <summary>
    /// 货物清单里的一格：显示一件未入包的物品（图标 + 名称），并作为拖入背包的起点。
    /// 必须实现 IDragHandler（哪怕空实现）：EventSystem 用 GetEventHandler&lt;IDragHandler&gt;(命中物) 决定 pointerDrag，
    /// 只实现 IBeginDragHandler 的组件永远收不到拖拽。
    /// 起手后把 pointerDrag 改指组合根——清单格随后可能被置空/复用，松手不能挂在这一格上。
    /// </summary>
    public class GridBagLootSlotView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        [SerializeField] private Image iconImage;

        [SerializeField] private TMP_Text nameText;

        private GridBagDemo owner;

        private GridItemDef def;

        public GridItemDef Def => def;

        /// <summary>绑定一块清单格（图标 + 名称）</summary>
        /// <param name="host">参数名：host，类型：GridBagDemo，用途：组合根</param>
        /// <param name="itemDef">参数名：itemDef，类型：GridItemDef，用途：该格物品（null = 空格）</param>
        /// <param name="icon">参数名：icon，类型：Sprite，用途：图标</param>
        public void Bind(GridBagDemo host, GridItemDef itemDef, Sprite icon)
        {
            owner = host;
            def = itemDef;
            if (iconImage != null)
            {
                iconImage.sprite = icon;
                iconImage.enabled = icon != null;
            }
            if (nameText != null) nameText.SetText(itemDef != null ? itemDef.DisplayName : string.Empty);
            gameObject.SetActive(itemDef != null);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (def == null || eventData.button != PointerEventData.InputButton.Left) return;
            owner?.BeginDragFromLoot(this, eventData);
        }

        public void OnDrag(PointerEventData eventData)
        {
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (eventData.pointerDrag == gameObject) owner?.OnEndDrag(eventData);
        }
    }
}