using UnityEngine;
using UnityEngine.EventSystems;

namespace GridBagDemo
{
    /// <summary>
    /// 拖拽事件目标（挂在背包视图节点上）。
    /// 起手时把 pointerDrag 指到这里：EBeginDrag 只能发生在射线命中对象上，
    /// 而后续的 OnDrag/OnEndDrag 必须由另一个对象承接——否则 EventSystem 会把 pointerDrag 指回背包视图，
    /// 背包视图在 BeginDrag 里改 pointerDrag 等于自指，物品会拖不动。
    /// </summary>
    public class GridBagDragTarget : MonoBehaviour, IDragHandler, IEndDragHandler
    {
        [SerializeField] private GridBagDemo owner;

        /// <summary>绑定宿主组合根</summary>
        public void Configure(GridBagDemo demo) => owner = demo;

        public void OnDrag(PointerEventData eventData) => owner?.OnBagDrag(eventData);

        public void OnEndDrag(PointerEventData eventData) => owner?.OnBagEndDrag(eventData);
    }
}