using TMPro;

using UnityEngine;
using UnityEngine.UI;

namespace GridBagDemo
{
    /// <summary>
    /// 背包里一件物品的视图：形状衬底 + 图标 + 重量角标。
    /// 形状衬底是构建期按「物品 × 朝向」烘焙好的 Sprite（非矩形物品的空透镜靠它标出真实占位）；
    /// 运行时只负责摆位置和按朝向换图，不生成任何图元。
    /// 本组件不持有数据引用：每次渲染都由 <see cref="GridBagView"/> 把数据对象传进来，
    /// 拖动中途数据被换位/丢弃时不会读到过期状态。
    /// </summary>
    public class GridBagItemView : MonoBehaviour
    {
        [SerializeField] private Image shapeImage;

        [SerializeField] private Image iconImage;

        [SerializeField] private TMP_Text weightText;

        /// <summary>图标在包围盒内的边距（留出形状衬底的描边）</summary>
        private const float IconInset = 6f;

        /// <summary>
        /// 按数据层当前状态渲染（位置、尺寸、形状图、图标、重量）。
        /// </summary>
        /// <param name="item">参数名：item，类型：GridItem，用途：要渲染的物品</param>
        /// <param name="cols">参数名：cols，类型：int，用途：网格列数</param>
        /// <param name="rows">参数名：rows，类型：int，用途：网格行数</param>
        /// <param name="cellSize">参数名：cellSize，类型：float，用途：格子边长</param>
        /// <param name="spacing">参数名：spacing，类型：float，用途：格子间距</param>
        /// <param name="shape">参数名：shape，类型：Sprite，用途：当前朝向的形状衬底</param>
        /// <param name="icon">参数名：icon，类型：Sprite，用途：物品图标</param>
        public void Render(GridItem item, int cols, int rows, float cellSize, float spacing, Sprite shape, Sprite icon)
        {
            if (item == null) return;

            Rect rect = GridBagLayout.ItemRect(item, cols, rows, cellSize, spacing);
            var body = (RectTransform)transform;
            body.sizeDelta = rect.size;
            body.anchoredPosition = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);

            if (shapeImage != null)
            {
                shapeImage.sprite = shape;
                shapeImage.enabled = shape != null;
            }

            if (iconImage != null)
            {
                iconImage.sprite = icon;
                iconImage.enabled = icon != null;

                // 图标在包围盒内等比缩进；长条物品的图标再压到不超过短边 1.6 倍，避免图标被拉成一条
                Vector2Int slots = GridBagLayout.RotatedSize(item.Def, item.Rotation);
                float width = Mathf.Max(1f, rect.width - IconInset * 2f);
                float height = Mathf.Max(1f, rect.height - IconInset * 2f);
                if (slots.x > slots.y) width = Mathf.Min(width, height * 1.6f);
                else if (slots.y > slots.x) height = Mathf.Min(height, width * 1.6f);

                RectTransform iconRect = iconImage.rectTransform;
                iconRect.sizeDelta = new Vector2(width, height);
                iconRect.anchoredPosition = Vector2.zero;
            }

            if (weightText != null) weightText.SetText(item.Def.Weight.ToString());
        }
    }
}
