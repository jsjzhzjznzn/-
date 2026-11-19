using System;

using Game.Equipment;
using System.Collections.Generic;

using MieMieFrameWork.Business;

using Game.Bag;

using Common.Network;

using Cysharp.Threading.Tasks;

using SkierFramework;

using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 背包界面控制器（beibao Canvas）
/// 布局：羊皮纸底图（PanelSkin 的 bg_bag）、左上标题 + 标题金线、右上"x/容量 格"与圆形关闭徽记、
/// 左侧竖排页签（武器背包 / 道具背包）、中部 7 列滚动槽位区、金褐竖分隔饰、
/// 右区预览底板 + 物品详情 + 使用/丢弃、左下"加物品/整理"、底部状态栏、正中金色加载指示器
/// 槽位区虚拟化（VirtualScrollList）：500 格 = 84 行（一行 6 格），只实例化视口内十来行，
/// 滚动时行从对象池复用换绑；格子交互（点击选中/拖拽换位）只对可见行生效
/// 换位拖拽：拖影只活到松手，且起手行可能中途被换绑/回收，所以源槽位按值缓存、
/// 拖拽回调挂在本物体（不会被列表复用）上，而不是挂在格子上
/// 数据全部来自 BagManager（服务端权威快照），本类只做渲染与交互转发
/// </summary>
public class BagView : UIView, IDragHandler, IEndDragHandler
{

    /// <summary>背包事件订阅令牌（关闭页面时释放）</summary>
    private IDisposable bagUpdatedToken;

    /// <summary>背包状态文案订阅令牌</summary>
    private IDisposable bagStatusToken;
    // ──────────────────────────────
    // 序列化引用（编辑器搭建脚本注入）
    // ──────────────────────────────

    [Header("面板")]
    [ControlBinding]
    public Button closeButton;

    [Header("页签（武器背包 / 道具背包）")]
    [ControlBinding]
    public Button tabWeaponButton;

    [ControlBinding]
    public Button tabItemButton;

    [Header("顶部")]
    [ControlBinding]
    public TMP_Text titleText;

    [ControlBinding]
    public TMP_Text capacityText;

    [Header("槽位滚动区")]
    [ControlBinding]
    public ScrollRect slotScroll;

    [ControlBinding("SlotScroll/Viewport/Content")]
    public RectTransform listContent;

    [ControlBinding("SlotScroll/Viewport/Content/RowTemplate")]
    public RectTransform rowTemplate;

    [Header("右区详情")]
    [ControlBinding("PreviewSquare/PreviewIcon")]
    public Image previewIcon;

    [ControlBinding]
    public TMP_Text detailNameText;

    [ControlBinding]
    public TMP_Text detailMetaText;

    [ControlBinding]
    public TMP_Text detailDescText;

    [ControlBinding]
    public Button useButton;

    [ControlBinding]
    public Button discardButton;

    [Header("底部与提示")]
    [ControlBinding]
    public Button grantButton;

    [ControlBinding]
    public Button tidyButton;

    [ControlBinding]
    public TMP_Text statusText;

    [ControlBinding]
    public RectTransform loadingIndicator;

    [ControlBinding]
    public RectTransform dragLayer;

    [Header("视觉参数")]
    public int columns = 6;

    /// <summary>格子边长（行高；与背景网格瓦片同尺寸）</summary>
    public float cellSize = 67f;

    /// <summary>槽位常态色（乘在格子底贴图上：中性白底 × 土黄 = 羊皮纸格子；选中/已装备再乘金色）</summary>
    public Color slotFrameColor = new(0.722f, 0.621f, 0.451f, 1f);

    /// <summary>槽位选中边框色（金色高亮）</summary>
    public Color slotSelectedColor = new(0.91f, 0.77f, 0.42f, 1f);

    /// <summary>加载指示器颜色（金色圆弧）</summary>
    public Color spinnerColor = new(0.69f, 0.55f, 0.31f, 1f);

    /// <summary>滚轮每格滚动几行（1=一格滚一行仍偏慢；默认 3 翻得快，Inspector 可随时调）</summary>
    public float wheelRowsPerNotch = 3f;

    [Header("页签外观（换贴图 + 换字色 + 左侧金色指示条）")]
    /// <summary>选中页签贴图（暖金纸片）</summary>
    public Sprite tabActiveSprite;

    /// <summary>未选中页签贴图（米纸片）</summary>
    public Sprite tabIdleSprite;

    /// <summary>选中页签字色（深褐）</summary>
    public Color tabActiveTextColor = new(0.29f, 0.23f, 0.13f, 1f);

    /// <summary>未选中页签字色（次级褐）</summary>
    public Color tabIdleTextColor = new(0.549f, 0.522f, 0.471f, 1f);

    [Header("换位拖拽")]
    /// <summary>光标进到视口上下多宽的带里开始自动翻页（占视口高度比例）</summary>
    [Range(0.02f, 0.45f)] public float dragAutoScrollEdge = 0.16f;

    /// <summary>贴边自动滚动的满速（行/秒；越贴边越快，此为贴到最外沿的速度）</summary>
    public float dragAutoScrollRowsPerSecond = 5f;

    // ──────────────────────────────
    // 私有字段
    // ──────────────────────────────

    /// <summary>槽位区虚拟列表（行 = 6 格）</summary>
    private VirtualScrollList slotVirtual;

    /// <summary>当前选中槽位（-1 = 未选中）</summary>
    private int selectedSlot = -1;

    /// <summary>页签：0 = 武器背包（[0, 容量)，有上限），1 = 道具背包（[容量, …)，无上限）</summary>
    private const int TabWeapon = 0;
    private const int TabItem = 1;

    /// <summary>当前页签（默认武器页）</summary>
    private int activeTab = TabWeapon;

    /// <summary>"加物品"按钮的循环游标（按配置表顺序发放，便于验证）</summary>
    private int grantCursor;

    /// <summary>拖拽光标的临时图标</summary>
    private Image dragGhost;

    /// <summary>换位起手的槽位下标（-1 = 无拖拽）。行对象会被虚拟列表复用换绑，拖拽全程只认这个值拷贝</summary>
    private int dragSourceIndex = -1;

    /// <summary>换位期间缓存的光标屏幕坐标与事件相机（每帧做贴边判定，拖拽事件不保证每帧都来）</summary>
    private Vector2 dragPointerPosition;

    private Camera dragPointerCamera;

    /// <summary>滚轮灵敏度（SetupSlotList 算一次；换位期间置 0，松手还原）</summary>
    private float wheelSensitivity = 200f;

    /// <summary>行间距（与 Configure 传入值一致，贴边滚动按行换算距离要用）</summary>
    private const float SlotRowSpacing = 4f;

    /// <summary>射线命中缓冲（避免每次拖拽分配）</summary>
    private static readonly List<RaycastResult> raycastResults = new();

    // ──────────────────────────────
    // 槽位区数据换算
    // ──────────────────────────────

    /// <summary>总格数：当前页签的格数（武器区 = 服务端容量；道具区 = 动态容量；未加载时用 60 占位）</summary>
    private int TotalSlots
    {
        get
        {
            BagManager bag = BagManager.Instance;
            if (bag == null || bag.Capacity <= 0) return columns * 10;
            return activeTab == TabWeapon ? bag.Capacity : bag.ItemCapacity;
        }
    }

    /// <summary>当前页签的起始槽位（武器区从 0 开始；道具区从武器区容量开始）</summary>
    private int RegionStart
    {
        get
        {
            BagManager bag = BagManager.Instance;
            if (bag == null || bag.Capacity <= 0) return 0;
            return activeTab == TabWeapon ? 0 : bag.Capacity;
        }
    }

    /// <summary>总行数（每行 columns 格，向上取整；500 格 = 84 行）</summary>
    private int TotalRows => (TotalSlots + columns - 1) / columns;

    // ──────────────────────────────
    // Unity 生命周期
    // ──────────────────────────────

    /// <summary>绑定完成后的一次性初始化：生成程序化贴图 + 订阅数据层 + 建虚拟列表（只做一次）</summary>
    public override void OnInit(UIControlData uIControlData, UIViewController controller)
    {
        base.OnInit(uIControlData, controller);

        // 贴图生成依赖绑定后的 serialized 引用，必须放在 base.OnInit 之后（放 Awake 会拿到 null）
        // 行列数/格子尺寸以行模板为准（预制体重建时不会因为序列化默认值 6/67 和 8 列模板打架）
        if (rowTemplate != null && rowTemplate.childCount > 0)
        {
            columns = rowTemplate.childCount;
            if (rowTemplate.rect.height > 1f) cellSize = rowTemplate.rect.height;
        }

        ApplyProceduralSprites();
        if (rowTemplate != null) rowTemplate.gameObject.SetActive(false);

        if (BagManager.Instance != null)
        {
            bagUpdatedToken = BagManager.Bus.Subscribe(BagEvents.Updated, HandleBagUpdated);
            bagStatusToken = BagManager.Bus.Subscribe(BagEvents.Status, SetStatus);
        }
    }

    /// <summary>每次打开重新接线（关闭时框架走 OnRemoveListener 摘掉）</summary>
    public override void OnAddListener()
    {
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);
        if (useButton != null) useButton.onClick.AddListener(OnUseClicked);
        if (discardButton != null) discardButton.onClick.AddListener(OnDiscardClicked);
        if (grantButton != null) grantButton.onClick.AddListener(OnGrantClicked);
        if (tidyButton != null) tidyButton.onClick.AddListener(OnTidyClicked);
        if (tabWeaponButton != null) tabWeaponButton.onClick.AddListener(OnTabWeaponClicked);
        if (tabItemButton != null) tabItemButton.onClick.AddListener(OnTabItemClicked);
    }

    public override void OnRemoveListener()
    {
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (useButton != null) useButton.onClick.RemoveListener(OnUseClicked);
        if (discardButton != null) discardButton.onClick.RemoveListener(OnDiscardClicked);
        if (grantButton != null) grantButton.onClick.RemoveListener(OnGrantClicked);
        if (tidyButton != null) tidyButton.onClick.RemoveListener(OnTidyClicked);
        if (tabWeaponButton != null) tabWeaponButton.onClick.RemoveListener(OnTabWeaponClicked);
        if (tabItemButton != null) tabItemButton.onClick.RemoveListener(OnTabItemClicked);
    }

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);

        // 虚拟列表必须在面板真正激活、矩形有效之后建（放 OnInit 会按 0 尺寸算成 1 行）
        if (slotVirtual == null) SetupSlotList();

        selectedSlot = -1;
        activeTab = TabWeapon;   // 每次打开回到武器页（与"x/容量 格"文案一致，避免上次页签造成误解）
        SetStatus(string.Empty);
        RefreshAll();
        BagManager.Instance?.RequestBag();   // 打开即拉最新列表（期间显示加载指示器）
    }

    public override void OnClose()
    {
        base.OnClose();
        CancelDrag();
        selectedSlot = -1;
    }

    private void OnDestroy()
    {
        if (BagManager.Instance == null) return;
        bagUpdatedToken?.Dispose();
        bagStatusToken?.Dispose();
    }

    private void Update()
    {
        // 加载指示器旋转（不受 timeScale 影响，暂停时也保持转动）
        if (loadingIndicator != null && loadingIndicator.gameObject.activeSelf)
        {
            loadingIndicator.Rotate(0f, 0f, -200f * Time.unscaledDeltaTime);
        }

        UpdateDragAutoScroll();
    }

    // ──────────────────────────────
    // 面板开关
    // ──────────────────────────────

    private void OnCloseClicked()
    {
        UIManager.Instance.Close(UIType.BagView).Forget();
    }

    // ──────────────────────────────
    // 页签（武器背包 / 道具背包）
    // ──────────────────────────────

    private void OnTabWeaponClicked() => SwitchTab(TabWeapon);

    private void OnTabItemClicked() => SwitchTab(TabItem);

    /// <summary>切页签：换一个槽位区间重绑列表；选中项与拖影都属于上一个区间，必须清掉</summary>
    private void SwitchTab(int tab)
    {
        if (activeTab == tab) return;
        activeTab = tab;
        selectedSlot = -1;
        CancelDrag();
        SetStatus(string.Empty);
        RefreshAll();   // 内部会 RefreshData（行数随页签变化）
    }

    /// <summary>页签选中态：换贴图 + 换标签字色 + 只让当前页签显示左侧金色指示条</summary>
    private void RefreshTabLook()
    {
        SetTabLook(tabWeaponButton, activeTab == TabWeapon);
        SetTabLook(tabItemButton, activeTab == TabItem);
    }

    private void SetTabLook(Button button, bool selected)
    {
        if (button == null) return;

        if (button.targetGraphic != null)
        {
            Image chip = button.targetGraphic as Image;
            if (chip != null)
            {
                Sprite sprite = selected ? tabActiveSprite : tabIdleSprite;
                if (sprite != null)
                {
                    chip.sprite = sprite;
                    chip.type = Image.Type.Sliced;
                }
                chip.color = Color.white;   // 两态的区别在贴图里，不再靠染底色
            }
        }

        TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.color = selected ? tabActiveTextColor : tabIdleTextColor;

        Transform indicator = button.transform.Find("Indicator");
        if (indicator != null) indicator.gameObject.SetActive(selected);
    }

    // ──────────────────────────────
    // 虚拟列表接入
    // ──────────────────────────────

    /// <summary>
    /// 接入槽位区虚拟列表的方法
    /// 等高行（一行 = 6 格）、单池；CreateItem 克隆 RowTemplate 并给 6 个格子补 Bind（宿主与颜色），
    /// BindSlotRow 每次换绑 6 个格子的下标与内容（格子对象随行复用）
    /// </summary>
    private void SetupSlotList()
    {
        if (slotScroll == null || rowTemplate == null || listContent == null) return;

        // 滚轮灵敏度 = 每格滚 wheelRowsPerNotch 行（行高按 cellSize 计；UGUI 默认 1 慢到几乎不动）
        wheelSensitivity = Mathf.Max(cellSize, 40f) * Mathf.Max(wheelRowsPerNotch, 0.1f);
        slotScroll.scrollSensitivity = wheelSensitivity;

        slotVirtual = slotScroll.GetComponent<VirtualScrollList>();
        if (slotVirtual == null) slotVirtual = slotScroll.gameObject.AddComponent<VirtualScrollList>();
        slotVirtual.Configure(8f, 8f, SlotRowSpacing);
        slotVirtual.GetItemCount = () => TotalRows;
        slotVirtual.GetItemHeight = _ => cellSize;
        slotVirtual.CreateItem = _ =>
        {
            RectTransform row = Instantiate(rowTemplate, listContent);
            row.gameObject.SetActive(true);
            foreach (BagSlotView view in row.GetComponentsInChildren<BagSlotView>(true))
            {
                view.Bind(this, slotFrameColor, slotSelectedColor);
            }
            return row;
        };
        slotVirtual.BindItem = BindSlotRow;
    }

    /// <summary>行换绑：第 row 行的 6 个格子分别对应槽位 row*6+0..5；超出容量的格子隐藏（末行不满）</summary>
    private void BindSlotRow(int row, RectTransform rowRect)
    {
        BagManager bag = BagManager.Instance;
        int baseIndex = row * columns;
        int cellCount = Mathf.Min(rowRect.childCount, columns);
        for (int c = 0; c < cellCount; c++)
        {
            BagSlotView view = rowRect.GetChild(c).GetComponent<BagSlotView>();
            if (view == null) continue;

            int slotIndex = RegionStart + baseIndex + c;
            if (baseIndex + c >= TotalSlots)
            {
                view.gameObject.SetActive(false);
                continue;
            }

            view.gameObject.SetActive(true);
            view.SetSlotIndex(slotIndex);

            BagSlotInfo slot = bag?.GetSlot(slotIndex);
            Sprite icon = null;
            int count = 0;
            bool equipped = false;
            if (slot != null && slot.Count > 0)
            {
                count = slot.Count;
                icon = ItemIconLoader.Get(bag.GetConfig(slot.ItemId)?.IconKey);
                // 装备实例正穿在某个角色身上 → 金色边框标注（服务端实例表是唯一真相，本地只读缓存）
                IEquipmentService gameEquipment = GameHub.Get<IEquipmentService>();
                equipped = slot.InstanceId > 0
                           && gameEquipment != null
                           && gameEquipment.GetEquippedChar(slot.InstanceId) >= 0;
            }
            view.Render(icon, count, slotIndex == selectedSlot, equipped);
        }
    }

    // ──────────────────────────────
    // 渲染
    // ──────────────────────────────

    /// <summary>背包数据落地：先收掉进行中的换位拖影（下标可能已被这次刷新改判），再重绑可见行</summary>
    private void HandleBagUpdated()
    {
        CancelDrag();
        RefreshAll();
    }

    /// <summary>全量刷新：容量文案 + 虚拟列表重绑可见行 + 右区详情 + 加载指示器</summary>
    private void RefreshAll()
    {
        BagManager bag = BagManager.Instance;
        if (bag == null) return;

        if (capacityText != null)
        {
            capacityText.SetText(activeTab == TabWeapon
                ? $"{bag.UsedWeaponCount}/{bag.Capacity} 格"
                : $"{bag.UsedItemCount} 件（不限格）");
        }
        RefreshTabLook();
        if (loadingIndicator != null) loadingIndicator.gameObject.SetActive(bag.IsLoading);

        // 容量/数据变化都走整表重绑（可见行就十来个，代价可忽略；不可见行滚到时自然换绑）
        slotVirtual?.RefreshData();
        RefreshDetail();
    }

    /// <summary>右区详情：预览大图 + 名称 + 类型/稀有度 + 描述；未选中时给出占位文案</summary>
    private void RefreshDetail()
    {
        BagManager bag = BagManager.Instance;
        BagSlotInfo slot = bag?.GetSlot(selectedSlot);
        ItemConfigInfo config = slot != null ? bag.GetConfig(slot.ItemId) : null;

        if (previewIcon != null)
        {
            previewIcon.sprite = config != null ? ItemIconLoader.Get(config.IconKey) : null;
            previewIcon.enabled = previewIcon.sprite != null;
        }

        if (detailNameText != null) detailNameText.SetText(config != null ? config.Name : "未选择物品");
        if (detailMetaText != null)
        {
            string meta = config != null
                ? $"{TypeName(config.ItemType)} · {RarityName(config.Rarity)}"
                : string.Empty;
            // 已穿戴的装备在详情里点名，避免"金色边框"被误读成稀有度强调
            if (config != null && slot != null && slot.InstanceId > 0 && EquipmentManager.Instance != null)
            {
                IEquipmentService gameEquipment = GameHub.Get<IEquipmentService>();
                int equippedChar = gameEquipment != null ? gameEquipment.GetEquippedChar(slot.InstanceId) : -1;
                if (equippedChar >= 0) meta += $" · 已装备：{BattleHeroCatalog.GetDisplayName(equippedChar)}";
            }
            detailMetaText.SetText(meta);
        }
        if (detailDescText != null) detailDescText.SetText(config != null ? config.Desc : string.Empty);

        if (useButton != null) useButton.interactable = config != null;
        if (discardButton != null) discardButton.interactable = config != null;
    }

    private void SetStatus(string content)
    {
        if (statusText != null) statusText.SetText(content ?? string.Empty);
    }

    // ──────────────────────────────
    // 交互：选择 / 拖拽换位
    // ──────────────────────────────

    /// <summary>点击槽位：有物品则选中（右区显示详情），点空槽取消选择</summary>
    public void OnSlotClicked(int index)
    {
        BagManager bag = BagManager.Instance;
        selectedSlot = bag != null && bag.HasItem(index) ? index : -1;
        RefreshAll();
    }

    /// <summary>槽位是否可发起物品拖拽（空槽不吞拖拽，转给滚动区）</summary>
    public bool IsSlotDraggable(int index)
        => GameHub.Get<IBagService>()?.HasItem(index) ?? false;

    /// <summary>把拖拽事件转投滚动区（拖空白格子 = 滚动长列表）</summary>
    public void RedirectDragToScroll(PointerEventData eventData)
    {
        if (slotScroll == null) return;
        eventData.pointerDrag = slotScroll.gameObject;
        ExecuteEvents.Execute(slotScroll.gameObject, eventData, ExecuteEvents.beginDragHandler);
    }

    /// <summary>
    /// 开始拖拽：只有非空槽位能拖，生成跟随光标的图标拖影
    /// 起手时把槽位下标按值存进 dragSourceIndex——格子对象是虚拟列表复用的，拖拽途中列表一动，
    /// view.SlotIndex 就变成别的格子了，后续一律不回查 view
    /// </summary>
    public void OnSlotBeginDrag(BagSlotView view, PointerEventData eventData)
    {
        BagManager bag = BagManager.Instance;
        if (bag == null || dragLayer == null || view == null) return;

        int fromIndex = view.SlotIndex;
        if (!bag.HasItem(fromIndex)) return;

        Sprite icon = ItemIconLoader.Get(bag.GetConfig(bag.GetSlot(fromIndex).ItemId)?.IconKey);
        if (icon == null) return;

        GameObject ghostObject = new("DragGhost", typeof(RectTransform), typeof(Image));
        RectTransform ghostRect = (RectTransform)ghostObject.transform;
        ghostRect.SetParent(dragLayer, false);
        ghostRect.sizeDelta = new Vector2(cellSize * 0.8f, cellSize * 0.8f);

        dragGhost = ghostObject.GetComponent<Image>();
        dragGhost.sprite = icon;
        dragGhost.preserveAspect = true;
        dragGhost.raycastTarget = false;   // 拖影不能挡住落点射线
        dragGhost.color = new Color(1f, 1f, 1f, 0.85f);

        dragSourceIndex = fromIndex;
        dragPointerCamera = eventData.pressEventCamera;
        dragPointerPosition = eventData.position;
        UpdateGhostPosition(eventData);

        // 把拖拽后续事件抢到自己身上：格子是列表复用对象，贴边翻页会把起手行回收进池，
        // 挂在格子上就收不到 OnEndDrag（拖影泄漏、滚轮灵敏度也回不来）
        eventData.pointerDrag = gameObject;

        // 换位期间关掉滚轮：滚轮一动内容就平移，光标下的格子和用户看到的会错位。
        // 够远处的格子走"拖影贴视口边缘自动翻页"（UpdateDragAutoScroll）
        if (slotScroll != null) slotScroll.scrollSensitivity = 0f;
    }

    /// <summary>拖拽中：拖影跟随光标，并缓存光标位置供每帧贴边判定</summary>
    public void OnDrag(PointerEventData eventData)
    {
        if (dragSourceIndex < 0) return;
        dragPointerPosition = eventData.position;
        UpdateGhostPosition(eventData);
    }

    /// <summary>松手：按光标位置射线找落点（找不到/落回源格 = 取消），命中别的格子才请求服务端换位</summary>
    public void OnEndDrag(PointerEventData eventData)
    {
        if (dragSourceIndex < 0) return;

        int fromIndex = dragSourceIndex;
        ClearDragState();

        BagSlotView target = FindSlotUnderPointer(eventData);
        // 按下标比，不比 GameObject 引用：起手那个格子可能已被换绑到别的位置
        if (target == null || target.SlotIndex == fromIndex) return;

        BagManager.Instance?.SwapSlots(fromIndex, target.SlotIndex);
    }

    /// <summary>取消拖拽（面板关闭、或拖拽途中收到背包数据推送）：只收拖影，不发换位</summary>
    private void CancelDrag()
    {
        if (dragSourceIndex < 0) return;
        ClearDragState();
    }

    private void ClearDragState()
    {
        if (dragGhost != null)
        {
            Destroy(dragGhost.gameObject);
            dragGhost = null;
        }
        dragSourceIndex = -1;
        if (slotScroll != null) slotScroll.scrollSensitivity = wheelSensitivity;
    }

    /// <summary>
    /// 换位拖拽中的贴边自动翻页：光标进入视口上/下边缘带就按行速滚动内容，越贴边越快
    /// （刚入带给一档基础速度，避免边缘处手感发死）。
    /// 滚动会换绑行对象，但换位认的是 dragSourceIndex 值拷贝，不受影响
    /// </summary>
    private void UpdateDragAutoScroll()
    {
        if (dragSourceIndex < 0 || slotScroll == null || dragAutoScrollRowsPerSecond <= 0f) return;

        RectTransform viewport = slotScroll.viewport;
        if (viewport == null) return;

        float viewportHeight = viewport.rect.height;
        float contentHeight = slotVirtual != null
            ? slotVirtual.ContentHeight
            : (listContent != null ? listContent.rect.height : 0f);
        float scrollable = contentHeight - viewportHeight;
        if (scrollable <= 0f) return;   // 内容不满一屏，没得滚

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(viewport, dragPointerPosition, dragPointerCamera, out Vector2 local)) return;

        // 边缘带按视口 rect 的实际上下沿算（视口 pivot 不一定是中心）
        float band = Mathf.Clamp(dragAutoScrollEdge, 0.02f, 0.45f) * viewportHeight;
        bool nearTop = local.y > viewport.rect.yMax - band;
        bool nearBottom = local.y < viewport.rect.yMin + band;
        if (!nearTop && !nearBottom) return;

        float into = nearTop ? local.y - (viewport.rect.yMax - band) : (viewport.rect.yMin + band) - local.y;
        float pixels = dragAutoScrollRowsPerSecond * (cellSize + SlotRowSpacing) * Time.unscaledDeltaTime
                       * (0.35f + 0.65f * Mathf.Clamp01(into / band));
        // verticalNormalizedPosition：1 = 顶，0 = 底；贴顶要往前翻（看更早的行）
        float direction = nearTop ? 1f : -1f;
        slotScroll.verticalNormalizedPosition = Mathf.Clamp01(slotScroll.verticalNormalizedPosition + direction * pixels / scrollable);
    }

    private void UpdateGhostPosition(PointerEventData eventData)
    {
        if (dragGhost == null || dragLayer == null) return;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            dragLayer, eventData.position, eventData.pressEventCamera, out Vector2 localPoint);
        ((RectTransform)dragGhost.transform).anchoredPosition = localPoint;
    }

    /// <summary>按指针位置射线，找命中的槽位条目（命中子物体时向上取父级）</summary>
    private static BagSlotView FindSlotUnderPointer(PointerEventData eventData)
    {
        if (EventSystem.current == null) return null;
        raycastResults.Clear();
        EventSystem.current.RaycastAll(eventData, raycastResults);
        foreach (RaycastResult result in raycastResults)
        {
            BagSlotView view = result.gameObject.GetComponentInParent<BagSlotView>();
            if (view != null) return view;
        }
        return null;
    }

    // ──────────────────────────────
    // 交互：底部按钮
    // ──────────────────────────────

    private void OnUseClicked()
    {
        if (selectedSlot < 0) return;
        BagManager.Instance?.UseItem(selectedSlot, 1);
    }

    private void OnDiscardClicked()
    {
        if (selectedSlot < 0) return;
        BagManager.Instance?.DiscardItem(selectedSlot, 1);
    }

    /// <summary>"加物品"：按配置表顺序循环发放（堆叠物品发 3 个，便于观察数量角标）</summary>
    private void OnGrantClicked()
    {
        BagManager bag = BagManager.Instance;
        if (bag == null || bag.AllConfigs.Count == 0)
        {
            SetStatus("物品配置未加载，请先打开背包");
            return;
        }

        if (grantCursor >= bag.AllConfigs.Count) grantCursor = 0;
        ItemConfigInfo config = bag.AllConfigs[grantCursor];
        grantCursor = (grantCursor + 1) % bag.AllConfigs.Count;
        bag.Grant(config.ItemId, Mathf.Clamp(config.MaxStack, 1, 3));
    }

    private void OnTidyClicked()
    {
        BagManager.Instance?.Tidy();
    }

// ──────────────────────────────
    // 程序化贴图（不依赖美术资源：加载圆环）
    // 格子底 / 页签 / 按钮 / 关闭徽记 / 预览底板 / 分隔饰的贴图由 BagViewSkinBuilder 烘进预制体
    // （菜单 Tools/UI/重建背包界面），运行期不再生成，避免每帧/每次开面板再算一遍
    // 静态缓存：圆环贴图整个会话只创建一次；
    // 判空必须用 == 而不是 ?? / ?.（Unity 的 == 对已销毁对象返回真，域重载关闭时残留的已销毁引用会被安全重建）
    // ──────────────────────────────

    /// <summary>加载圆环缓存（尺寸固定 40）</summary>
    private static Sprite cachedRing;

    private void ApplyProceduralSprites()
    {
        if (loadingIndicator == null) return;

        Image spinner = loadingIndicator.GetComponent<Image>();
        if (spinner == null) return;

        if (cachedRing == null) cachedRing = CreateRingSprite(40, 5f);
        spinner.sprite = cachedRing;
        spinner.color = spinnerColor;
        spinner.raycastTarget = false;
    }

    /// <summary>生成圆环（270° 弧，留缺口让旋转可见）：白色，颜色由 Image.color 染</summary>
    private Sprite CreateRingSprite(int size, float thickness)
    {
        Texture2D texture = new(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        float center = (size - 1) * 0.5f;
        float outer = center;
        float inner = center - thickness;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;   // -180..180
                bool inRing = distance <= outer && distance >= inner;
                bool inArc = angle >= -90f && angle <= 180f;         // 270° 弧，右下方留缺口
                texture.SetPixel(x, y, inRing && inArc ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    // ──────────────────────────────
    // 文案映射
    // ──────────────────────────────

    private static string TypeName(int itemType) => itemType switch
    {
        1 => "消耗品",
        2 => "材料",
        3 => "武器",
        _ => "其他",
    };

    private static string RarityName(int rarity) => rarity switch
    {
        1 => "普通",
        2 => "优良",
        3 => "精良",
        4 => "稀有",
        5 => "传说",
        _ => "未知",
    };
}
