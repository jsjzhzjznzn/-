using System.Collections.Generic;

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GridBagDemo
{
    /// <summary>
    /// 网格背包视图：格子底、物品视图池、拖动落点预览，并把指针事件翻译成数据层调用。
    ///
    /// 命中判定统一走「屏幕坐标 → 本地坐标 → 格坐标」换算，物品视图不是射线目标。
    /// 物品视图挂在独立的子节点上：本节点作为拖拽起点，需要重置位置的话不能把物品甩到别的层级去。
    /// </summary>
    public class GridBagView : MonoBehaviour, IBeginDragHandler, IPointerClickHandler
    {
        [SerializeField] private RectTransform cellsRoot;

        [SerializeField] private RectTransform itemsRoot;

        [SerializeField] private GridBagItemView itemViewTemplate;

        [SerializeField] private Image previewImage;

        [SerializeField] private GridBagDragTarget dragTarget;

        [SerializeField] private Color emptyCellColor = new(0.16f, 0.17f, 0.20f, 1f);

        [SerializeField] private Color filledCellColor = new(0.11f, 0.12f, 0.14f, 1f);

        [SerializeField] private Color previewValidColor = new(0.35f, 0.78f, 0.45f, 0.55f);

        [SerializeField] private Color previewInvalidColor = new(0.90f, 0.30f, 0.28f, 0.55f);

        /// <summary>背包格子边长（构建器与运行时一致）</summary>
        public const float CellSize = 64f;

        /// <summary>格子间距</summary>
        public const float Spacing = 4f;

        private readonly List<GridBagItemView> viewPool = new();
        private readonly Dictionary<GridItem, GridBagItemView> views = new();
        private readonly List<Image> cellImages = new();

        private Vector2 grabOffset;
        private Canvas canvas;

        public GridBag Bag { get; private set; }

        /// <summary>宿主组合根（提供图标与形状 Sprite、处理松手规则）</summary>
        public GridBagDemo Owner { get; private set; }

        /// <summary>是否正在拖动背包内的物品</summary>
        public bool IsDragging { get; private set; }

        /// <summary>当前拖动的物品（数据层此刻已把它摘出，松手再决定放回/丢弃）</summary>
        public GridItem DragItem { get; private set; }

        /// <summary>当前拖动起手时的锚点与朝向（回弹用）</summary>
        public Vector2Int DragStartAnchor { get; private set; }

        public int DragStartRotation { get; private set; }

        /// <summary>当前落点将要使用的朝向（拖动中按自适应规则算好，预览与落位共用同一个值）</summary>
        public int DropRotation { get; private set; }

        /// <summary>格子底数量（E2E 断言用）</summary>
        public int CellCount => cellImages.Count;

        public RectTransform RectTransform => (RectTransform)transform;

        /// <summary>
        /// 初始化：绑定数据与宿主，生成格子底与初始物品视图。
        /// </summary>
        /// <param name="owner">参数名：owner，类型：GridBagDemo，用途：组合根</param>
        /// <param name="bag">参数名：bag，类型：GridBag，用途：背包数据</param>
        public void Build(GridBagDemo owner, GridBag bag)
        {
            Owner = owner;
            Bag = bag;
            if (dragTarget != null) dragTarget.Configure(owner);
            if (itemViewTemplate != null) itemViewTemplate.gameObject.SetActive(false);
            BuildCells();
            RebuildItems();
        }

        // ──────────────────────────────
        // 渲染
        // ──────────────────────────────

        private void BuildCells()
        {
            cellImages.Clear();
            if (cellsRoot == null || Bag == null) return;

            ((RectTransform)cellsRoot).sizeDelta = GridBagLayout.GridSize(Bag.Width, Bag.Height, CellSize, Spacing);

            for (int y = 0; y < Bag.Height; y++)
            {
                for (int x = 0; x < Bag.Width; x++)
                {
                    var go = new GameObject($"Cell_{x}_{y}", typeof(RectTransform), typeof(Image));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(cellsRoot, false);
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(CellSize, CellSize);
                    Vector2 topLeft = GridBagLayout.CellToLocal(new Vector2Int(x, y), Bag.Width, Bag.Height, CellSize, Spacing);
                    rt.anchoredPosition = new Vector2(topLeft.x + CellSize * 0.5f, topLeft.y - CellSize * 0.5f);

                    var image = go.GetComponent<Image>();
                    image.sprite = Owner != null ? Owner.CellSprite : null;
                    image.type = Image.Type.Sliced;
                    image.raycastTarget = false;
                    image.color = emptyCellColor;
                    cellImages.Add(image);
                }
            }
        }

        /// <summary>按数据层状态全量刷新物品视图（物品少，直接全量；视图按物品对象复用，不重建）</summary>
        public void RebuildItems()
        {
            if (Bag == null) return;

            foreach (KeyValuePair<GridItem, GridBagItemView> pair in views)
            {
                if (!Bag.Items.Contains(pair.Key) && pair.Value != null) pair.Value.gameObject.SetActive(false);
            }

            foreach (GridItem item in Bag.Items)
            {
                GridBagItemView view = GetView(item);
                view.gameObject.SetActive(true);
                view.Render(item, Bag.Width, Bag.Height, CellSize, Spacing,
                    Owner != null ? Owner.GetShapeSprite(item.Def, item.Rotation) : null,
                    Owner != null ? Owner.GetIconSprite(item.Def) : null);
            }

            RefreshCellColors();
        }

        private void RefreshCellColors()
        {
            if (Bag == null) return;
            for (int y = 0; y < Bag.Height; y++)
            {
                for (int x = 0; x < Bag.Width; x++)
                {
                    Image image = cellImages[y * Bag.Width + x];
                    if (image != null) image.color = Bag.GetOccupant(x, y) != null ? filledCellColor : emptyCellColor;
                }
            }
        }

        private GridBagItemView GetView(GridItem item)
        {
            if (views.TryGetValue(item, out GridBagItemView view) && view != null) return view;

            view = viewPool.Find(v => v != null && !v.gameObject.activeSelf);
            if (view == null)
            {
                view = Instantiate(itemViewTemplate, itemsRoot);
                viewPool.Add(view);
            }
            views[item] = view;
            return view;
        }

        // ──────────────────────────────
        // 坐标换算
        // ──────────────────────────────

        /// <summary>屏幕坐标 → 格坐标（走 ScreenPointToLocalPointInRectangle，不受画布缩放影响）</summary>
        public Vector2Int ScreenToCell(Vector2 screenPosition, Camera eventCamera)
        {
            Vector2 local = ScreenToLocal(screenPosition, eventCamera);
            return GridBagLayout.LocalToCell(local, Bag.Width, Bag.Height, CellSize, Spacing);
        }

        /// <summary>屏幕坐标是否落在背包区域内</summary>
        public bool IsInside(Vector2 screenPosition, Camera eventCamera)
            => RectTransformUtility.RectangleContainsScreenPoint(RectTransform, screenPosition, ResolveCamera(eventCamera));

        /// <summary>屏幕坐标 → 本区域本地坐标（camera 为空时用画布相机兜底）</summary>
        public Vector2 ScreenToLocal(Vector2 screenPosition, Camera eventCamera)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(RectTransform, screenPosition, ResolveCamera(eventCamera), out Vector2 local);
            return local;
        }

        /// <summary>
        /// 取实际参与射线换算的相机：ScreenSpaceOverlay 画布传 null 即可，ScreenSpaceCamera 画布必须给世界相机。
        /// 传入 null 会被 RectTransformUtility 当作「屏幕坐标即本地坐标」处理，导致落点整体错位。
        /// </summary>
        private Camera ResolveCamera(Camera eventCamera)
        {
            if (eventCamera != null) return eventCamera;
            if (canvas == null) canvas = GetComponentInParent<Canvas>();
            return canvas != null ? canvas.worldCamera : null;
        }

        /// <summary>
        /// 按「光标在物品内的相对偏移」反推左上角锚点：抓哪拖哪，不会一抓就跳到右下角。
        /// 结果允许越界（负数/超出），由数据层校验并给出非法预览，松手时回弹。
        /// </summary>
        public Vector2Int AnchorFromPointer(Vector2 screenPosition, Camera eventCamera)
        {
            Vector2 local = ScreenToLocal(screenPosition, eventCamera);
            float pitch = CellSize + Spacing;
            Vector2 gridSize = GridBagLayout.GridSize(Bag.Width, Bag.Height, CellSize, Spacing);
            int x = Mathf.RoundToInt((local.x - grabOffset.x + gridSize.x * 0.5f) / pitch);
            int y = Mathf.RoundToInt((gridSize.y * 0.5f - local.y - grabOffset.y) / pitch);
            return new Vector2Int(x, y);
        }

        /// <summary>光标相对物品左上角的本地偏移</summary>
        private Vector2 ComputeGrabOffset(GridItem item, Vector2 screenPosition, Camera eventCamera)
        {
            Rect rect = GridBagLayout.ItemRect(item, Bag.Width, Bag.Height, CellSize, Spacing);
            Vector2 local = ScreenToLocal(screenPosition, eventCamera);
            return new Vector2(local.x - rect.x, (rect.y + rect.height) - local.y);
        }

        /// <summary>某格物品视图的屏幕矩形（E2E 计算落点时用）</summary>
        public Rect ItemScreenRect(GridItem item, Camera eventCamera)
        {
            Rect local = GridBagLayout.ItemRect(item, Bag.Width, Bag.Height, CellSize, Spacing);
            Vector3 min = RectTransform.TransformPoint(new Vector3(local.xMin, local.yMin));
            Vector3 max = RectTransform.TransformPoint(new Vector3(local.xMax, local.yMax));
            Vector2 a = RectTransformUtility.WorldToScreenPoint(eventCamera, min);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(eventCamera, max);
            float xMin = Mathf.Min(a.x, b.x);
            float yMin = Mathf.Min(a.y, b.y);
            return new Rect(xMin, yMin, Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        // ──────────────────────────────
        // 指针事件
        // ──────────────────────────────

        /// <summary>
        /// 开始拖动：命中有物品的格子就把它从数据层摘出（视图隐藏），并把 pointerDrag 交到 <see cref="dragTarget"/>。
        /// </summary>
        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Bag == null || Owner == null || eventData.button != PointerEventData.InputButton.Left) return;

            Vector2Int cell = ScreenToCell(eventData.position, ResolveCamera(eventData));
            GridItem item = Bag.GetOccupant(cell.x, cell.y);
            if (item == null) return;

            grabOffset = ComputeGrabOffset(item, eventData.position, ResolveCamera(eventData));
            DragStartAnchor = item.Anchor;
            DragStartRotation = item.Rotation;
            DropRotation = item.Rotation;

            if (views.TryGetValue(item, out GridBagItemView view) && view != null) view.gameObject.SetActive(false);
            if (previewImage != null)
            {
                previewImage.rectTransform.sizeDelta = GridBagLayout.ItemRect(item, Bag.Width, Bag.Height, CellSize, Spacing).size;
                previewImage.rectTransform.anchoredPosition = GridBagLayout.ItemRect(item, Bag.Width, Bag.Height, CellSize, Spacing).center;
            }
            Bag.Remove(item);
            RefreshCellColors();

            DragItem = item;
            IsDragging = true;
            eventData.pointerDrag = dragTarget != null ? dragTarget.gameObject : null;
            Owner.BeginDrag(item, view, eventData);
            UpdatePreview(eventData);
        }

        /// <summary>
        /// 取事件相机：Overlay 画布为 null，Camera 画布用世界相机；
        /// 按下时记录的 pressEventCamera 可能为空（例如鼠标由另一根相机驱动），统一在本类里兜底。
        /// </summary>
        private Camera ResolveCamera(PointerEventData eventData)
        {
            if (eventData.pressEventCamera != null) return eventData.pressEventCamera;
            Canvas canvas = GetComponentInParent<Canvas>();
            return canvas != null ? canvas.worldCamera : null;
        }

        /// <summary>点击（未拖动）：右键或中键旋转</summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.dragging) return;
            if (eventData.button != PointerEventData.InputButton.Right && eventData.button != PointerEventData.InputButton.Middle) return;

            Vector2Int cell = ScreenToCell(eventData.position, ResolveCamera(eventData));
            Owner?.RotateItemAt(cell.x, cell.y);
        }

        /// <summary>组合根收到拖动事件后回调：更新落点预览（放不下时按自适应朝向显示旋转后的预览）</summary>
        public void UpdatePreview(PointerEventData eventData)
        {
            if (DragItem == null || previewImage == null) return;

            if (!IsInside(eventData.position, ResolveCamera(eventData)))
            {
                previewImage.enabled = false;
                return;
            }

            Vector2Int anchor = AnchorFromPointer(eventData.position, ResolveCamera(eventData));
            DropRotation = Bag.ResolveRotation(DragItem, anchor, DragItem.Rotation);
            Rect rect = GridBagLayout.PreviewRect(DragItem, anchor, DropRotation, Bag.Width, Bag.Height, CellSize, Spacing);
            previewImage.rectTransform.sizeDelta = rect.size;
            previewImage.rectTransform.anchoredPosition = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            previewImage.color = Bag.CanPlace(DragItem, anchor, DropRotation) ? previewValidColor : previewInvalidColor;
            previewImage.enabled = true;
        }

        /// <summary>结束拖动：把落点信息交给组合根判定，然后刷新视图并收起预览</summary>
        public void EndDrag(PointerEventData eventData)
        {
            if (!IsDragging || DragItem == null) return;

            GridItem item = DragItem;
            Vector2Int anchor = AnchorFromPointer(eventData.position, ResolveCamera(eventData));
            bool inside = IsInside(eventData.position, ResolveCamera(eventData));

            IsDragging = false;
            DragItem = null;
            if (previewImage != null) previewImage.enabled = false;

            Owner.EndDrag(item, anchor, inside, DragStartAnchor, DragStartRotation, DropRotation);
            RebuildItems();
        }
    }
}
