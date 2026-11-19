using System.Collections.Generic;
using UnityEngine;

namespace GridBagDemo
{
    /// <summary>
    /// 网格背包的数据层：格子占用表 + 放置合法性校验 + 放置/移除/旋转/互换/自动找位。
    /// 不依赖任何 MonoBehaviour 与 UI，纯内存模型，可直接单测。
    /// 占用表存「物品引用而不是布尔」，因此查占用与还原都是 O(1)，也不需要额外的索引同步。
    /// </summary>
    public class GridBag
    {
        /// <summary>物品集合（不保证顺序）</summary>
        private readonly List<GridItem> items = new();

        /// <summary>占用表：Width × Height，null = 空</summary>
        private readonly GridItem[] cells;

        public GridBag(int width, int height)
        {
            Width = Mathf.Max(1, width);
            Height = Mathf.Max(1, height);
            cells = new GridItem[Width * Height];
        }

        public int Width { get; }

        public int Height { get; }

        public List<GridItem> Items => items;

        /// <summary>已占用的格子数（按形状格数计，不算包围盒里的空透镜）</summary>
        public int UsedCells { get; private set; }

        /// <summary>总格数</summary>
        public int Capacity => Width * Height;

        /// <summary>全部物品的重量之和</summary>
        public int TotalWeight
        {
            get
            {
                int sum = 0;
                foreach (GridItem item in items) sum += item.Def != null ? item.Def.Weight : 0;
                return sum;
            }
        }

        /// <summary>取某格占用的物品</summary>
        /// <param name="x">参数名：x，类型：int，用途：列</param>
        /// <param name="y">参数名：y，类型：int，用途：行</param>
        /// <returns>返回值类型：GridItem，用途：占用者（空格为 null）</returns>
        public GridItem GetOccupant(int x, int y)
        {
            if (!InBounds(x, y)) return null;
            return cells[y * Width + x];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        /// <summary>
        /// 能否把物品以「锚点 + 朝向」放进背包。
        /// 只校验形状为 1 的格子：包围盒内的空透镜允许被别的物品占用（非矩形物品互相咬合的来源）；
        /// 自身已占的格子视为合法，方便原地旋转与原地移动。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：待放物品</param>
        /// <param name="anchor">参数名：anchor，类型：Vector2Int，用途：左上角格坐标</param>
        /// <param name="rotation">参数名：rotation，类型：int，用途：朝向 0~3</param>
        /// <returns>返回值类型：bool，用途：是否可放</returns>
        public bool CanPlace(GridItem item, Vector2Int anchor, int rotation)
        {
            if (item?.Def == null) return false;
            int sx = rotation % 2 == 0 ? item.Def.ShapeWidth : item.Def.ShapeHeight;
            int sy = rotation % 2 == 0 ? item.Def.ShapeHeight : item.Def.ShapeWidth;

            if (anchor.x < 0 || anchor.y < 0 || anchor.x + sx > Width || anchor.y + sy > Height) return false;

            for (int y = 0; y < sy; y++)
            {
                for (int x = 0; x < sx; x++)
                {
                    if (!item.IsFilled(x, y, rotation)) continue;
                    GridItem occupant = cells[(anchor.y + y) * Width + (anchor.x + x)];
                    if (occupant != null && occupant != item) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 把物品放进背包（调用前必须先用 <see cref="CanPlace"/> 校验；本方法不重复校验）。
        /// 物品若已在背包里（先 Remove 过则不在），这里只写占用表。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：待放物品</param>
        /// <param name="anchor">参数名：anchor，类型：Vector2Int，用途：左上角格坐标</param>
        /// <param name="rotation">参数名：rotation，类型：int，用途：朝向 0~3</param>
        public void Place(GridItem item, Vector2Int anchor, int rotation)
        {
            item.Anchor = anchor;
            item.Rotation = ((rotation % 4) + 4) % 4;
            if (!items.Contains(item)) items.Add(item);

            for (int y = 0; y < item.SizeY; y++)
            {
                for (int x = 0; x < item.SizeX; x++)
                {
                    if (!item.IsFilled(x, y, item.Rotation)) continue;
                    cells[(anchor.y + y) * Width + (anchor.x + x)] = item;
                    UsedCells++;
                }
            }
        }

        /// <summary>把物品从背包移除（写空它占的格子）</summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：要移除的物品</param>
        public void Remove(GridItem item)
        {
            if (item == null) return;

            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] != item) continue;
                cells[i] = null;
                UsedCells--;
            }
            items.Remove(item);
        }

        /// <summary>
        /// 把「已经拿出背包」的 incoming 与背包内的 target 互换位置。
        /// incoming 由调用方在起手时摘出（不在占用表里），所以这里只需摘 target：
        /// 两者都能落在对方的位置上才交换，任一放不下就只还原 target，整次操作不留中间态。
        /// </summary>
        /// <param name="incoming">参数名：incoming，类型：GridItem，用途：正被拖动的物品（已不在背包内）</param>
        /// <param name="target">参数名：target，类型：GridItem，用途：落点压着的物品（仍在背包内）</param>
        /// <param name="incomingAnchor">参数名：incomingAnchor，类型：Vector2Int，用途：incoming 起手时的锚点</param>
        /// <param name="incomingRotation">参数名：incomingRotation，类型：int，用途：incoming 起手时的朝向</param>
        /// <returns>返回值类型：bool，用途：是否交换成功</returns>
        public bool TryReplaceAt(GridItem incoming, GridItem target, Vector2Int incomingAnchor, int incomingRotation)
        {
            if (incoming == null || target == null || incoming == target) return false;
            if (!items.Contains(target)) return false;

            Vector2Int targetAnchor = target.Anchor;
            int targetRotation = target.Rotation;

            Remove(target);
            if (CanPlace(incoming, targetAnchor, targetRotation) && CanPlace(target, incomingAnchor, incomingRotation))
            {
                Place(incoming, targetAnchor, targetRotation);
                Place(target, incomingAnchor, incomingRotation);
                return true;
            }

            Place(target, targetAnchor, targetRotation);
            return false;
        }

        /// <summary>
        /// 在 preferred 附近（由近到远）找第一个能放下 item 的位置；找不到返回 false。
        /// 环形搜索：按到 preferred 的格距离由小到大遍历所有锚点，保证「吸位」结果是离光标最近的那个。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：待放物品</param>
        /// <param name="rotation">参数名：rotation，类型：int，用途：朝向</param>
        /// <param name="preferred">参数名：preferred，类型：Vector2Int，用途：光标所在的理想锚点</param>
        /// <param name="found">参数名：found，类型：out Vector2Int，用途：找到的锚点</param>
        /// <returns>返回值类型：bool，用途：是否找到位置</returns>
        public bool TryFindNear(GridItem item, int rotation, Vector2Int preferred, out Vector2Int found)
            => TryFindNear(item, rotation, preferred, out found, out _);

        /// <summary>
        /// 同上，但把「实际落位用的朝向」一并带出：首选朝向找不到位置时按自适应顺序换向再找，
        /// 用于货物清单拖入（这类物品没有「当前朝向」，横竖都该能放）。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：待放物品</param>
        /// <param name="rotation">参数名：rotation，类型：int，用途：首选朝向</param>
        /// <param name="preferred">参数名：preferred，类型：Vector2Int，用途：光标附近的理想锚点</param>
        /// <param name="found">参数名：found，类型：out Vector2Int，用途：找到的锚点</param>
        /// <param name="foundRotation">参数名：foundRotation，类型：out int，用途：该锚点可用的朝向</param>
        /// <returns>返回值类型：bool，用途：是否找到位置</returns>
        public bool TryFindNear(GridItem item, int rotation, Vector2Int preferred, out Vector2Int found, out int foundRotation)
        {
            found = preferred;
            foundRotation = ((rotation % 4) + 4) % 4;
            if (item?.Def == null) return false;
            if (CanPlace(item, preferred, foundRotation)) return true;

            // 先按首选朝向找最近的合法锚点；找不到再依次换朝向（90° → 270° → 180°）
            for (int i = 0; i < AutoRotationOrder.Length; i++)
            {
                int candidateRotation = (foundRotation + AutoRotationOrder[i]) % 4;
                if (TryFindNearest(item, candidateRotation, preferred, out Vector2Int nearest))
                {
                    found = nearest;
                    foundRotation = candidateRotation;
                    return true;
                }
            }
            return false;
        }

        /// <summary>在指定朝向下找离 preferred 最近的合法锚点</summary>
        private bool TryFindNearest(GridItem item, int rotation, Vector2Int preferred, out Vector2Int found)
        {
            found = preferred;
            int sx = rotation % 2 == 0 ? item.Def.ShapeWidth : item.Def.ShapeHeight;
            int sy = rotation % 2 == 0 ? item.Def.ShapeHeight : item.Def.ShapeWidth;

            int bestDistance = int.MaxValue;
            Vector2Int best = default;
            bool any = false;
            for (int y = 0; y <= Height - sy; y++)
            {
                for (int x = 0; x <= Width - sx; x++)
                {
                    int distance = (x - preferred.x) * (x - preferred.x) + (y - preferred.y) * (y - preferred.y);
                    if (distance >= bestDistance) continue;
                    var candidate = new Vector2Int(x, y);
                    if (!CanPlace(item, candidate, rotation)) continue;
                    bestDistance = distance;
                    best = candidate;
                    any = true;
                }
            }

            found = best;
            return any;
        }

        /// <summary>
        /// 自适应朝向：当前朝向放得下就用它，否则按「顺时针 90° → 顺时针 270° → 180°」依次试，
        /// 返回第一个放得下的朝向；四种都不行时返回首选朝向（由调用方判定为非法并提示/回弹）。
        /// 先试 90° 是因为长条物品在横竖之间的空档最常见，也避免出现上下颠倒的观感。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：待放物品</param>
        /// <param name="anchor">参数名：anchor，类型：Vector2Int，用途：左上角格坐标</param>
        /// <param name="preferred">参数名：preferred，类型：int，用途：首选朝向（物品当前朝向）</param>
        /// <returns>返回值类型：int，用途：可用于放置的朝向</returns>
        public int ResolveRotation(GridItem item, Vector2Int anchor, int preferred)
        {
            preferred = ((preferred % 4) + 4) % 4;
            for (int i = 0; i < AutoRotationOrder.Length; i++)
            {
                int candidate = (preferred + AutoRotationOrder[i]) % 4;
                if (CanPlace(item, anchor, candidate)) return candidate;
            }
            return preferred;
        }

        /// <summary>自适应朝向的尝试顺序（相对首选朝向的增量）</summary>
        private static readonly int[] AutoRotationOrder = { 0, 1, 3, 2 };

        /// <summary>
        /// 清空并重建：按配置顺序依次放置，放不下的跳过（构建器与运行时初始化共用）。
        /// </summary>
        /// <param name="defs">参数名：defs，类型：IReadOnlyList&lt;GridItemDef&gt;，用途：初始物品（按顺序贪心放置）</param>
        /// <returns>返回值类型：List&lt;GridItem&gt;，用途：实际放进去的物品</returns>
        public List<GridItem> FillFromDefs(IReadOnlyList<GridItemDef> defs)
        {
            var placed = new List<GridItem>();
            if (defs == null) return placed;

            foreach (GridItemDef def in defs)
            {
                if (def == null) continue;
                var item = new GridItem(def);
                if (!TryFindNear(item, 0, Vector2Int.zero, out Vector2Int anchor, out int rotation)) continue;
                Place(item, anchor, rotation);
                placed.Add(item);
            }
            return placed;
        }
    }
}
