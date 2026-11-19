using UnityEngine;

namespace GridBagDemo
{
    /// <summary>
    /// 网格背包的物品定义：形状位图 + 展示信息。
    /// 形状用「行优先的 0/1 位图」描述：1 = 占格，0 = 空格（非矩形物品的凹口/阶梯都在这里表达）。
    /// 矩形物品直接用 Init(width, height) 生成全 1 位图；L 形、阶梯形等传自定义掩码。
    /// 朝向不写进定义：物品实例自己记 Rotation（0~3），由 GridBag 在旋转后重新投影形状。
    /// </summary>
    [CreateAssetMenu(fileName = "GridItemDef", menuName = "网格背包 Demo/物品定义")]
    public class GridItemDef : ScriptableObject
    {
        [SerializeField] private string itemId;

        [SerializeField] private string displayName;

        [SerializeField] private string iconKey;

        [SerializeField] private int weight;

        [SerializeField] private int shapeWidth;

        [SerializeField] private int shapeHeight;

        [SerializeField] private int[] shape = System.Array.Empty<int>();

        public string ItemId => itemId;

        public string DisplayName => displayName;

        public string IconKey => iconKey;

        public int Weight => weight;

        public int ShapeWidth => shapeWidth;

        public int ShapeHeight => shapeHeight;

        /// <summary>形状位图（长度 = ShapeWidth × ShapeHeight，行优先，1 = 占格）</summary>
        public int[] Shape => shape;

        /// <summary>形状占用的格子数（与朝向无关）</summary>
        public int CellCount
        {
            get
            {
                int count = 0;
                foreach (int v in shape) if (v != 0) count++;
                return count;
            }
        }

        /// <summary>设置展示信息</summary>
        /// <param name="id">参数名：id，类型：string，用途：唯一标识（用图标名，便于排查）</param>
        /// <param name="name">参数名：name，类型：string，用途：中文名</param>
        /// <param name="icon">参数名：icon，类型：string，用途：ItemIconLoader 的图标键</param>
        /// <param name="weightValue">参数名：weightValue，类型：int，用途：重量（展示用）</param>
        public void SetInfo(string id, string name, string icon, int weightValue)
        {
            itemId = id;
            displayName = name;
            iconKey = icon;
            weight = weightValue;
        }

        /// <summary>设置矩形形状（宽 × 高 全占格）</summary>
        /// <param name="width">参数名：width，类型：int，用途：形状宽</param>
        /// <param name="height">参数名：height，类型：int，用途：形状高</param>
        public void SetRectShape(int width, int height)
        {
            int[] mask = new int[width * height];
            for (int i = 0; i < mask.Length; i++) mask[i] = 1;
            SetShape(width, height, mask);
        }

        /// <summary>设置自定义形状（行优先，1 = 占格）</summary>
        /// <param name="width">参数名：width，类型：int，用途：形状宽</param>
        /// <param name="height">参数名：height，类型：int，用途：形状高</param>
        /// <param name="mask">参数名：mask，类型：int[]，用途：行优先形状位图</param>
        public void SetShape(int width, int height, int[] mask)
        {
            shapeWidth = width;
            shapeHeight = height;
            shape = mask;
        }
    }
}
