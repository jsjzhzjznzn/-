using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Game.MapGen
{
    /// <summary>
    /// 逐格填充的矩形画布，等价于 Cocos 的 Graphics.fillRect：一次提交里把若干带色矩形合成一张网格。
    /// Tuanjie 的 uGUI 分叉没有 Cocos 那种 Graphics 组件，因此用 MaskableGraphic 自绘代替。
    /// </summary>
    public class TileRectPainter : MaskableGraphic
    {
        /// <summary>uGUI 单个 Mesh 的顶点上限，超过就要拆成多张子画布</summary>
        public const int MaxVerticesPerMesh = 65000;

        private struct RectCell
        {
            public float x;
            public float y;
            public float width;
            public float height;
            public Color color;
        }

        private readonly List<RectCell> cells = new List<RectCell>(4096);

        /// <summary>当前累积的矩形数量</summary>
        public int CellCount => cells.Count;

        /// <summary>清空全部矩形</summary>
        public void Clear()
        {
            if (cells.Count == 0)
            {
                return;
            }
            cells.Clear();
            SetVerticesDirty();
        }

        /// <summary>追加一个矩形（坐标以本控件的 pivot 为原点）</summary>
        public void AddRect(float x, float y, float width, float height, Color color)
        {
            cells.Add(new RectCell { x = x, y = y, width = width, height = height, color = color });
        }

        /// <summary>追加后统一提交，等价于一次重新生成</summary>
        public void Commit()
        {
            SetVerticesDirty();
        }

        /// <summary>把矩形写入指定区间的顶点缓冲（start 为起始下标，count 为数量）</summary>
        public void PopulateRange(VertexHelper vh, int start, int count)
        {
            vh.Clear();
            WriteRange(vh, start, count);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            // 网格上限由 MapManager 拆张保证，这里再兜一次底
            PopulateRange(vh, 0, Mathf.Min(cells.Count, MaxVerticesPerMesh / 4));
        }

        private void WriteRange(VertexHelper vh, int start, int count)
        {
            int end = Mathf.Min(start + count, cells.Count);
            for (int i = start; i < end; i++)
            {
                RectCell cell = cells[i];
                float left = cell.x;
                float bottom = cell.y;
                float right = cell.x + cell.width;
                float top = cell.y + cell.height;

                int first = vh.currentVertCount;
                vh.AddVert(new Vector3(left, bottom), cell.color, new Vector2(0, 0));
                vh.AddVert(new Vector3(left, top), cell.color, new Vector2(0, 1));
                vh.AddVert(new Vector3(right, top), cell.color, new Vector2(1, 1));
                vh.AddVert(new Vector3(right, bottom), cell.color, new Vector2(1, 0));

                vh.AddTriangle(first, first + 1, first + 2);
                vh.AddTriangle(first, first + 2, first + 3);
            }
        }
    }
}
