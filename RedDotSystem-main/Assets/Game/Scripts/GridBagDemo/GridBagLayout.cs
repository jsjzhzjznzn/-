using UnityEngine;

namespace GridBagDemo
{
    /// <summary>
    /// 格坐标 ↔ UI 本地坐标的换算（纯静态，无状态）。
    /// 约定：格坐标原点 (0,0) 是左上角格；pitch = 格子边长 + 间距，所以一格物品的视图宽高 = 边长，
    /// 多格物品 = 格数 × pitch − 间距（格子自身的包围盒），相邻物品之间自然留出间距。
    /// </summary>
    public static class GridBagLayout
    {
        /// <summary>锚点格左上角在网格区域本地坐标（以网格区域中心为原点）</summary>
        /// <param name="anchor">参数名：anchor，类型：Vector2Int，用途：左上角格坐标</param>
        /// <param name="cols">参数名：cols，类型：int，用途：网格列数</param>
        /// <param name="rows">参数名：rows，类型：int，用途：网格行数</param>
        /// <param name="cellSize">参数名：cellSize，类型：float，用途：格子边长</param>
        /// <param name="spacing">参数名：spacing，类型：float，用途：格子间距</param>
        /// <returns>返回值类型：Vector2，用途：本地坐标</returns>
        public static Vector2 CellToLocal(Vector2Int anchor, int cols, int rows, float cellSize, float spacing)
        {
            float pitch = cellSize + spacing;
            float totalWidth = cols * pitch - spacing;
            float totalHeight = rows * pitch - spacing;
            return new Vector2(
                -totalWidth * 0.5f + anchor.x * pitch,
                totalHeight * 0.5f - anchor.y * pitch);
        }

        /// <summary>本地坐标 → 格坐标（取最近格的左上角；超出范围会给出负值或越界值，由调用方裁剪）</summary>
        /// <param name="local">参数名：local，类型：Vector2，用途：网格区域本地坐标</param>
        /// <param name="cols">参数名：cols，类型：int，用途：网格列数</param>
        /// <param name="rows">参数名：rows，类型：int，用途：网格行数</param>
        /// <param name="cellSize">参数名：cellSize，类型：float，用途：格子边长</param>
        /// <param name="spacing">参数名：spacing，类型：float，用途：格子间距</param>
        /// <returns>返回值类型：Vector2Int，用途：格坐标</returns>
        public static Vector2Int LocalToCell(Vector2 local, int cols, int rows, float cellSize, float spacing)
        {
            float pitch = cellSize + spacing;
            float totalWidth = cols * pitch - spacing;
            float totalHeight = rows * pitch - spacing;
            int x = Mathf.RoundToInt((local.x + totalWidth * 0.5f) / pitch);
            int y = Mathf.RoundToInt((totalHeight * 0.5f - local.y) / pitch);
            return new Vector2Int(Mathf.Clamp(x, 0, Mathf.Max(0, cols - 1)), Mathf.Clamp(y, 0, Mathf.Max(0, rows - 1)));
        }

        /// <summary>整块网格区域的尺寸（锚点格视图可用，避免调用方重复算 -间距）</summary>
        public static Vector2 GridSize(int cols, int rows, float cellSize, float spacing)
            => new(cols * (cellSize + spacing) - spacing, rows * (cellSize + spacing) - spacing);

        /// <summary>朝向 → 旋转后的形状尺寸（宽, 高），格数</summary>
        public static Vector2Int RotatedSize(GridItemDef def, int rotation)
        {
            if (def == null) return Vector2Int.one;
            return rotation % 2 == 0
                ? new Vector2Int(def.ShapeWidth, def.ShapeHeight)
                : new Vector2Int(def.ShapeHeight, def.ShapeWidth);
        }

        /// <summary>
        /// 生成「从左上角锚点出发、贴住网格顶部」的视图本地坐标（顶部对齐，尺寸 = 旋转后格数）。
        /// </summary>
        public static Rect ItemRect(GridItem item, int cols, int rows, float cellSize, float spacing)
            => ItemRect(item, item.Rotation, cols, rows, cellSize, spacing);

        /// <summary>按指定朝向生成物品视图矩形（朝向由调用方决定，用于拖动时的自动转向预览）</summary>
        public static Rect ItemRect(GridItem item, int rotation, int cols, int rows, float cellSize, float spacing)
        {
            Vector2Int size = RotatedSize(item.Def, rotation);
            Vector2 topLeft = CellToLocal(item.Anchor, cols, rows, cellSize, spacing);
            float width = size.x * (cellSize + spacing) - spacing;
            float height = size.y * (cellSize + spacing) - spacing;
            return new Rect(topLeft.x, topLeft.y - height, width, height);
        }

        /// <summary>落点预览的视图矩形（锚点首次被拖动时会越界，这里不裁剪，让预览如实显示位置）</summary>
        public static Rect PreviewRect(GridItem item, Vector2Int anchor, int rotation, int cols, int rows, float cellSize, float spacing)
        {
            Vector2Int size = RotatedSize(item.Def, rotation);
            Vector2 topLeft = CellToLocal(anchor, cols, rows, cellSize, spacing);
            float width = size.x * (cellSize + spacing) - spacing;
            float height = size.y * (cellSize + spacing) - spacing;
            return new Rect(topLeft.x, topLeft.y - height, width, height);
        }
    }
}
