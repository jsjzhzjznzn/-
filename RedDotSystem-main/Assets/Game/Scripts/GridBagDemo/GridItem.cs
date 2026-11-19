using UnityEngine;

namespace GridBagDemo
{
    /// <summary>
    /// 背包里的一件物品实例：定义 + 朝向 + 左上角锚点。
    /// 形状投影由 <see cref="GridBag"/> 依据 Rotation 实时计算，实例本身不缓存格子列表——
    /// 旋转/移动只改这两个字段，避免「缓存没同步」造成的重复占格。
    /// </summary>
    public class GridItem
    {
        /// <summary>物品定义</summary>
        public GridItemDef Def { get; }

        /// <summary>朝向（0~3，每 +1 顺时针旋转 90°）</summary>
        public int Rotation { get; set; }

        /// <summary>左上角格坐标（只在背包内有效）</summary>
        public Vector2Int Anchor { get; set; }

        /// <summary>该物品在背包中的视图（由视图层挂接；数据层不使用）</summary>
        public object View { get; set; }

        public GridItem(GridItemDef def)
        {
            Def = def;
        }

        /// <summary>旋转后包围盒宽度（格）</summary>
        public int SizeX => Rotation % 2 == 0 ? Def.ShapeWidth : Def.ShapeHeight;

        /// <summary>旋转后包围盒高度（格）</summary>
        public int SizeY => Rotation % 2 == 0 ? Def.ShapeHeight : Def.ShapeWidth;

        /// <summary>
        /// 取「在给定朝向下」的形状格是否为占格。
        /// 朝向每 +1 顺时针转 90°，(0,0) 始终是左上角，旋转后包围盒宽高互换：
        /// r=0 原样、r=1 顺时针 90°、r=2 转 180°、r=3 顺时针 270°。
        /// </summary>
        /// <param name="x">参数名：x，类型：int，用途：旋转后包围盒内列</param>
        /// <param name="y">参数名：y，类型：int，用途：旋转后包围盒内行（0 = 顶行）</param>
        /// <param name="rotation">参数名：rotation，类型：int，用途：朝向 0~3</param>
        /// <returns>返回值类型：bool，用途：该格是否占格</returns>
        public bool IsFilled(int x, int y, int rotation)
        {
            int w = Def.ShapeWidth;
            int h = Def.ShapeHeight;
            int[] shape = Def.Shape;
            switch (((rotation % 4) + 4) % 4)
            {
                case 1: return shape[(h - 1 - x) * w + y] != 0;           // 顺时针 90°：包围盒 h × w
                case 2: return shape[(h - 1 - y) * w + (w - 1 - x)] != 0;  // 转 180°
                case 3: return shape[x * w + (w - 1 - y)] != 0;            // 顺时针 270°
                default: return shape[y * w + x] != 0;
            }
        }

        public override string ToString() => $"{Def?.ItemId}@{Anchor}(r{Rotation})";
    }
}
