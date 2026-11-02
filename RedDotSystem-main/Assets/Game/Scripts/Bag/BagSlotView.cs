using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 背包槽位格子（RowTemplate 内 6 个，虚拟列表按行复用，下标随 BindSlotRow 改绑）
/// 职责：渲染一个槽位（图标 + 数量角标 + 选中高亮），把点击/拖拽事件转发给控制器
/// 拖拽换位：从有物品的槽位按下 → 控制器生成跟随光标的拖影并把后续拖拽事件接走（pointerDrag 改成控制器物体）、
///           松手时按光标射线找落点槽位 → 请求服务端换位；
///           空槽不吞拖拽——事件重定向给滚动区，拖空白处也能滚动长列表
/// 注意：格子随行复用，SlotIndex 会变，所以本组件只在 OnBeginDrag 读一次下标，不实现 OnDrag/OnEndDrag
/// 说明：格子尺寸由 BagView.cellSize 驱动（行模板内摆位与边框贴图同尺寸），本组件不改布局，只改颜色与内容
/// </summary>
public class BagSlotView : MonoBehaviour, IPointerClickHandler, IBeginDragHandler
{
    [Header("子物体引用（模板上注入）")]
    public Image frameImage;

    public Image iconImage;

    public TMP_Text countText;

    /// <summary>槽位下标（BindSlotRow 每次换绑时更新，格子被虚拟列表复用）</summary>
    public int SlotIndex { get; private set; }

    /// <summary>宿主控制器（克隆时写入）</summary>
    private BagView owner;

    /// <summary>普通边框色（控制器注入，与背景网格同色调）</summary>
    private Color normalFrameColor = Color.white;

    /// <summary>选中边框色（控制器注入）</summary>
    private Color selectedFrameColor = Color.white;

    /// <summary>已装备边框色（金色，与主页 GoldFrame 同色 #D9B25C）</summary>
    private static readonly Color EquippedFrameColor = new Color(0.851f, 0.698f, 0.361f, 1f);

    /// <summary>
    /// 绑定宿主控制器的方法（行克隆后由控制器调用一次；颜色不变，槽位下标走 SetSlotIndex 改绑）
    /// </summary>
    /// <param name="controller">参数名：controller，类型：BagView，用途：宿主控制器</param>
    /// <param name="normal">参数名：normal，类型：Color，用途：普通态边框色</param>
    /// <param name="selected">参数名：selected，类型：Color，用途：选中态边框色</param>
    public void Bind(BagView controller, Color normal, Color selected)
    {
        owner = controller;
        normalFrameColor = normal;
        selectedFrameColor = selected;
    }

    /// <summary>
    /// 改绑槽位下标的方法（虚拟列表行复用时每次换绑调用）
    /// </summary>
    /// <param name="index">参数名：index，类型：int，用途：槽位下标</param>
    public void SetSlotIndex(int index) => SlotIndex = index;

    /// <summary>
    /// 渲染槽位内容的方法（图标为 null 时隐藏图标；数量为 1 时不显示角标）
    /// </summary>
    /// <param name="icon">参数名：icon，类型：Sprite，用途：物品图标（空槽为 null）</param>
    /// <param name="count">参数名：count，类型：int，用途：数量（空槽为 0）</param>
    /// <param name="selected">参数名：selected，类型：bool，用途：是否选中态</param>
    /// <param name="equipped">参数名：equipped，类型：bool，用途：该件装备是否正穿在某个角色身上（金色边框标注）</param>
    public void Render(Sprite icon, int count, bool selected, bool equipped = false)
    {
        if (iconImage != null)
        {
            iconImage.sprite = icon;
            iconImage.enabled = icon != null;
        }

        if (countText != null)
        {
            countText.SetText(count > 1 ? count.ToString() : string.Empty);
        }

        if (frameImage != null)
        {
            frameImage.color = selected ? selectedFrameColor : (equipped ? EquippedFrameColor : normalFrameColor);
        }
    }

    // ──────────────────────────────
    // 交互（全部转发给控制器，本组件不碰数据）
    // ──────────────────────────────

    public void OnPointerClick(PointerEventData eventData) => owner?.OnSlotClicked(SlotIndex);

    /// <summary>
    /// 拖拽开始：空槽不吞事件——把 pointerDrag 换成滚动区并转投 beginDrag，让拖空白处也能滚动长列表；
    /// 有物品则交给控制器起手（控制器随后把 pointerDrag 改到自己身上，拖拽中/松手不再经过本格）
    /// </summary>
    public void OnBeginDrag(PointerEventData eventData)
    {
        if (owner == null) return;
        if (!owner.IsSlotDraggable(SlotIndex))
        {
            owner.RedirectDragToScroll(eventData);
            return;
        }
        owner.OnSlotBeginDrag(this, eventData);
    }
}
