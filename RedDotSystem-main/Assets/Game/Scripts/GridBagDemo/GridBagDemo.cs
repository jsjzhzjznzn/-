using System.Collections.Generic;

using TMPro;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GridBagDemo
{
    /// <summary>
    /// 网格背包 Demo 的组合根：持有背包数据、两套视图、拖影与提示，
    /// 并把「从哪里拖起（背包内 / 货物清单）」与「松手规则（放置 / 互换 / 丢弃 / 回弹）」集中在这里。
    ///
    /// 拖动中与松手事件分别由背包视图的 <see cref="GridBagDragTarget"/>、本对象自身（清单物品）承接：
    /// 起手后 pointerDrag 立刻从物品视图/清单格改指过去，途中格子被回收或置空也不会丢掉松手。
    /// </summary>
    public class GridBagDemo : MonoBehaviour, IDragHandler, IEndDragHandler
    {
        [Header("数据")]
        [SerializeField] private List<GridItemDef> initialItems = new();

        [SerializeField] private List<GridItemDef> lootItems = new();

        [SerializeField] private int bagWidth = 8;

        [SerializeField] private int bagHeight = 5;

        [Header("视图")]
        [SerializeField] private GridBagView bagView;

        [SerializeField] private GridBagLootView lootView;

        [SerializeField] private RectTransform dragLayer;

        [SerializeField] private Image dragGhost;

        [SerializeField] private Sprite cellSprite;

        [SerializeField] private TMP_Text capacityText;

        [SerializeField] private TMP_Text weightText;

        [SerializeField] private TMP_Text toastText;

        [SerializeField] private CanvasGroup toastGroup;

        [Header("形状衬底（构建期按物品 × 朝向烘焙，下标 = 物品序号 × 4 + 朝向）")]
        [SerializeField] private List<Sprite> shapeSprites = new();

        /// <summary>拖影边长（一格大小，表示整块物品）</summary>
        private const float GhostSize = 72f;

        private readonly Dictionary<GridItemDef, Sprite> iconCache = new();
        private readonly Dictionary<GridItemDef, int> defIndices = new();
        private readonly List<GridItemDef> allDefs = new();

        private GridItemDef dragDef;
        private int dragLootIndex = -1;
        private Canvas rootCanvas;

        public GridBag Bag { get; private set; }

        public GridBagView View => bagView;

        public GridBagLootView Loot => lootView;

        public Sprite CellSprite => cellSprite;

        /// <summary>取物品图标（走 ItemIconLoader，命中缓存）</summary>
        public Sprite GetIconSprite(GridItemDef def)
        {
            if (def == null) return null;
            if (iconCache.TryGetValue(def, out Sprite cached)) return cached;
            Sprite icon = ItemIconLoader.Get(def.IconKey);
            iconCache[def] = icon;
            return icon;
        }

        /// <summary>取物品某朝向的形状衬底（构建期烘焙；缺失时返回 null，视图退化为只画图标）</summary>
        public Sprite GetShapeSprite(GridItemDef def, int rotation)
        {
            if (def == null) return null;
            if (!defIndices.TryGetValue(def, out int index)) return null;

            int spriteIndex = index * 4 + ((rotation % 4) + 4) % 4;
            if (shapeSprites == null || spriteIndex < 0 || spriteIndex >= shapeSprites.Count) return null;
            return shapeSprites[spriteIndex];
        }

        private void Start() => BuildDemo();

        /// <summary>建立数据与视图（Start 调用；E2E 可直接调用）</summary>
        public void BuildDemo()
        {
            rootCanvas = GetComponentInParent<Canvas>();
            if (dragGhost != null) dragGhost.enabled = false;
            if (toastGroup != null) toastGroup.alpha = 0f;

            defIndices.Clear();
            allDefs.Clear();
            foreach (GridItemDef def in initialItems)
                if (def != null && !defIndices.ContainsKey(def)) { defIndices[def] = allDefs.Count; allDefs.Add(def); }
            foreach (GridItemDef def in lootItems)
                if (def != null && !defIndices.ContainsKey(def)) { defIndices[def] = allDefs.Count; allDefs.Add(def); }

            Bag = new GridBag(bagWidth, bagHeight);
            Bag.FillFromDefs(initialItems);

            bagView.Build(this, Bag);
            lootView.Build(this, lootItems);
            RefreshHeader();
        }

        // ──────────────────────────────
        // 背包内拖动（由 GridBagDragTarget 转发）
        // ──────────────────────────────

        /// <summary>背包内起手：物品已由视图从数据层摘出，这里准备拖影</summary>
        public void BeginDrag(GridItem item, GridBagItemView sourceView, PointerEventData eventData)
        {
            dragDef = item?.Def;
            dragLootIndex = -1;
            ShowGhost(item?.Def, eventData);
        }

        /// <summary>背包内拖动中</summary>
        public void OnBagDrag(PointerEventData eventData)
        {
            FollowPointer(eventData);
            bagView.UpdatePreview(eventData);
        }

        /// <summary>背包内松手</summary>
        public void OnBagEndDrag(PointerEventData eventData)
        {
            HideGhost();
            bagView.EndDrag(eventData);
        }

        // ──────────────────────────────
        // 货物清单拖动（本对象自身承接）
        // ──────────────────────────────

        /// <summary>从货物清单起手：记下来源格，生成拖影，事件接到本对象</summary>
        public void BeginDragFromLoot(GridBagLootSlotView slot, PointerEventData eventData)
        {
            dragDef = slot != null ? slot.Def : null;
            dragLootIndex = lootView.Slots.IndexOf(slot);
            ShowGhost(dragDef, eventData);
            eventData.pointerDrag = gameObject;
        }

        public void OnDrag(PointerEventData eventData) => FollowPointer(eventData);

        /// <summary>
        /// 清单物品松手：落在背包内就在光标附近自动吸位（由近到远找第一个可放位置）；
        /// 落在背包外视为取消，物品留在清单里。
        /// </summary>
        public void OnEndDrag(PointerEventData eventData)
        {
            HideGhost();
            GridItemDef def = dragDef;
            int lootIndex = dragLootIndex;
            dragDef = null;
            dragLootIndex = -1;
            if (def == null || Bag == null) return;

            if (!bagView.IsInside(eventData.position, PressCamera(eventData)))
            {
                ShowToast("已取消");
                return;
            }

            var item = new GridItem(def);
            Vector2Int preferred = bagView.ScreenToCell(eventData.position, PressCamera(eventData));
            // 清单物品没有「当前朝向」：横竖都试，取离光标最近的合法位置
            if (!Bag.TryFindNear(item, 0, preferred, out Vector2Int anchor, out int rotation))
            {
                ShowToast($"{def.DisplayName} 放不下了");
                return;
            }

            Bag.Place(item, anchor, rotation);
            lootView.Consume(lootIndex);
            bagView.RebuildItems();
            RefreshHeader();
            ShowToast($"{def.DisplayName} 已放入背包");
        }

        // ──────────────────────────────
        // 松手规则（背包内物品）
        // ──────────────────────────────

        /// <summary>
        /// 背包内物品松手：
        /// 在面板外 → 丢弃；
        /// 落点压着别的物品 → 尝试互换（两件都放得下才交换，否则整体回滚）；
        /// 落点合法 → 放下（朝向用拖动中算好的 <paramref name="dropRotation"/>，当前方向放不下时会自动转向）；
        /// 其余（越界/被挡）→ 回原位。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：被拖动的物品</param>
        /// <param name="anchor">参数名：anchor，类型：Vector2Int，用途：松手时的目标锚点</param>
        /// <param name="inside">参数名：inside，类型：bool，用途：松手位置是否在背包区域内</param>
        /// <param name="startAnchor">参数名：startAnchor，类型：Vector2Int，用途：起手锚点（回弹用）</param>
        /// <param name="startRotation">参数名：startRotation，类型：int，用途：起手朝向（回弹用）</param>
        /// <param name="dropRotation">参数名：dropRotation，类型：int，用途：落位朝向（自适应结果）</param>
        public void EndDrag(GridItem item, Vector2Int anchor, bool inside, Vector2Int startAnchor, int startRotation, int dropRotation)
        {
            if (item == null || Bag == null) return;

            if (!inside)
            {
                ShowToast($"已丢弃 {item.Def.DisplayName}");
                RefreshHeader();
                return;
            }

            GridItem target = TargetOccupant(item, anchor);
            if (target != null)
            {
                if (Bag.TryReplaceAt(item, target, startAnchor, dropRotation))
                {
                    ShowToast(dropRotation == startRotation
                        ? $"{item.Def.DisplayName} ⇄ {target.Def.DisplayName}"
                        : $"{item.Def.DisplayName} 已转向并互换");
                }
                else
                {
                    Bag.Place(item, startAnchor, startRotation);
                    ShowToast("空间不足，已回到原位");
                }
                RefreshHeader();
                return;
            }

            if (Bag.CanPlace(item, anchor, dropRotation))
            {
                Bag.Place(item, anchor, dropRotation);
                if (dropRotation != startRotation) ShowToast($"{item.Def.DisplayName} 已自动转向");
            }
            else
            {
                Bag.Place(item, startAnchor, startRotation);
                ShowToast("该位置放不下，已回到原位");
            }
            RefreshHeader();
        }

        /// <summary>
        /// 程序化放置（拾取/测试用）：把物品按「由近到远」放进背包。
        /// </summary>
        /// <param name="def">参数名：def，类型：GridItemDef，用途：物品定义</param>
        /// <param name="preferred">参数名：preferred，类型：Vector2Int，用途：理想锚点</param>
        /// <returns>返回值类型：GridItem，用途：放进去的物品（放不下为 null）</returns>
        public GridItem PlaceFromDef(GridItemDef def, Vector2Int preferred)
        {
            if (def == null || Bag == null) return null;
            var item = new GridItem(def);
            if (!Bag.TryFindNear(item, 0, preferred, out Vector2Int anchor)) return null;
            Bag.Place(item, anchor, 0);
            bagView.RebuildItems();
            RefreshHeader();
            return item;
        }

        /// <summary>
        /// 旋转背包内某格物品（右键/中键点击的真实入口也走这里）。
        /// 优先原地顺时针转 90°；原地放不下时在附近找位；仍放不下就保持原朝向并提示。
        /// </summary>
        /// <param name="x">参数名：x，类型：int，用途：列</param>
        /// <param name="y">参数名：y，类型：int，用途：行</param>
        /// <returns>返回值类型：bool，用途：是否旋转成功</returns>
        public bool RotateItemAt(int x, int y)
        {
            GridItem item = Bag?.GetOccupant(x, y);
            if (item == null) return false;

            Vector2Int anchor = item.Anchor;
            int previous = item.Rotation;
            int next = (previous + 1) % 4;

            Bag.Remove(item);
            bool placed;
            if (Bag.CanPlace(item, anchor, next))
            {
                Bag.Place(item, anchor, next);
                placed = true;
            }
            else if (Bag.TryFindNear(item, next, anchor, out Vector2Int found))
            {
                Bag.Place(item, found, next);
                placed = true;
            }
            else
            {
                Bag.Place(item, anchor, previous);
                placed = false;
            }

            bagView.RebuildItems();
            ShowToast(placed ? $"{item.Def.DisplayName} 已旋转" : "空间不足，无法旋转");
            RefreshHeader();
            return placed;
        }

        /// <summary>落点压着的「别的物品」（优先找真正重叠的那件，空透镜里的格子不算占用）</summary>
        private GridItem TargetOccupant(GridItem item, Vector2Int anchor)
        {
            for (int y = 0; y < item.SizeY; y++)
            {
                for (int x = 0; x < item.SizeX; x++)
                {
                    if (!item.IsFilled(x, y, item.Rotation)) continue;
                    GridItem occupant = Bag.GetOccupant(anchor.x + x, anchor.y + y);
                    if (occupant != null && occupant != item) return occupant;
                }
            }
            return null;
        }

        // ──────────────────────────────
        // 拖影与提示
        // ──────────────────────────────

        /// <summary>取事件相机：Overlay 画布为 null，Camera 画布用世界相机（按下时的相机可能为空，统一兜底）</summary>
        private Camera PressCamera(PointerEventData eventData)
        {
            if (eventData.pressEventCamera != null) return eventData.pressEventCamera;
            if (rootCanvas == null) rootCanvas = GetComponentInParent<Canvas>();
            return rootCanvas != null ? rootCanvas.worldCamera : null;
        }
        private void ShowGhost(GridItemDef def, PointerEventData eventData)
        {
            if (dragGhost == null || def == null) return;
            dragGhost.sprite = GetIconSprite(def);
            dragGhost.enabled = true;
            dragGhost.rectTransform.sizeDelta = new Vector2(GhostSize, GhostSize);
            FollowPointer(eventData);
        }

        private void FollowPointer(PointerEventData eventData)
        {
            if (dragGhost == null || dragLayer == null || !dragGhost.enabled) return;

            Camera camera = eventData.pressEventCamera != null ? eventData.pressEventCamera : (rootCanvas != null ? rootCanvas.worldCamera : null);
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(dragLayer, eventData.position, camera, out Vector2 local))
                dragGhost.rectTransform.anchoredPosition = local;
        }

        private void HideGhost()
        {
            if (dragGhost != null) dragGhost.enabled = false;
        }

        private void RefreshHeader()
        {
            if (Bag == null) return;
            if (capacityText != null) capacityText.SetText($"容量 {Bag.UsedCells} / {Bag.Capacity}");
            if (weightText != null) weightText.SetText($"重量 {Bag.TotalWeight}");
        }

        private void ShowToast(string message)
        {
            if (toastText == null) return;
            toastText.SetText(message);
            if (toastGroup != null) toastGroup.alpha = 1f;
            CancelInvoke(nameof(HideToast));
            Invoke(nameof(HideToast), 1.6f);
        }

        private void HideToast()
        {
            if (toastGroup != null) toastGroup.alpha = 0f;
        }
    }
}
