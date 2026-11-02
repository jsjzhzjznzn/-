using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGen
{
    /// <summary>
    /// WFC 的拼接模块：四个方向各有一条边（0 = 封闭，1 = 通道），
    /// 相邻两格只有在「朝向对方的那条边相等」时才允许挨着。
    /// </summary>
    public class WfcTile
    {
        /// <summary>边索引：0 上、1 右、2 下、3 左</summary>
        public const int SideCount = 4;
        public const int Up = 0;
        public const int Right = 1;
        public const int Down = 2;
        public const int Left = 3;

        public string Name;
        public Color Color;
        public int Weight;
        /// <summary>四条边的通道状态，1 表示这一侧有开口</summary>
        public int[] Edges = new int[SideCount];

        public WfcTile(string name, Color color, int weight, int up, int right, int down, int left)
        {
            Name = name;
            Color = color;
            Weight = weight;
            Edges[Up] = up;
            Edges[Right] = right;
            Edges[Down] = down;
            Edges[Left] = left;
        }

        public int EdgeAt(int side) => Edges[side];

        /// <summary>对侧边的索引：上与下相对，左与右相对</summary>
        public static int Opposite(int side) => (side + 2) % SideCount;

        public override string ToString() => Name;
    }

    /// <summary>
    /// 地牢拼接用的模块集。边编码 (上,右,下,左)，1 = 通道：
    /// 十字、丁字、直通、转角、死路各有若干朝向，外加一个四边封闭的实心块。
    /// </summary>
    public static class WfcTileset
    {
        public const int Solid = 0;
        public const int Room = 1;
        public const int Cross = 2;
        public const int Tee = 3;
        public const int Straight = 4;
        public const int Corner = 5;
        public const int DeadEnd = 6;

        /// <summary>构建默认模块集（索引与上面的常量一一对应）</summary>
        public static List<WfcTile> Create()
        {
            var tiles = new List<WfcTile>();

            // 实心：四面封闭，填充地图外围与封闭区域。
            // 权重直接决定空腔比例，实测 26/60/90/120/160 → 空腔 88/79/71/66/60%
            var wallColor = new Color(0.10f, 0.10f, 0.12f);
            tiles.Add(new WfcTile("Solid", wallColor, 120, 0, 0, 0, 0));
            // 单面开墙：只有一侧通向通道，其余三面是墙。有了它墙体才可能出现在地图内部
            tiles.Add(new WfcTile("Wall_U", wallColor, 7, 1, 0, 0, 0));
            tiles.Add(new WfcTile("Wall_R", wallColor, 7, 0, 1, 0, 0));
            tiles.Add(new WfcTile("Wall_D", wallColor, 7, 0, 0, 1, 0));
            tiles.Add(new WfcTile("Wall_L", wallColor, 7, 0, 0, 0, 1));
            // 房间：四面通透，是连通性的主干
            tiles.Add(new WfcTile("Room", new Color(0.58f, 0.52f, 0.45f), 34, 1, 1, 1, 1));
            // 十字路口
            tiles.Add(new WfcTile("Cross", new Color(0.50f, 0.46f, 0.42f), 8, 1, 1, 1, 1));

            // 丁字：缺一侧
            var teeColor = new Color(0.54f, 0.49f, 0.43f);
            tiles.Add(new WfcTile("Tee_UDL", teeColor, 10, 1, 0, 1, 1));
            tiles.Add(new WfcTile("Tee_UDR", teeColor, 10, 1, 1, 1, 0));
            tiles.Add(new WfcTile("Tee_URD", teeColor, 10, 1, 1, 0, 1));
            tiles.Add(new WfcTile("Tee_LRD", teeColor, 10, 0, 1, 1, 1));

            // 直通
            var straightColor = new Color(0.52f, 0.47f, 0.41f);
            tiles.Add(new WfcTile("Straight_V", straightColor, 16, 1, 0, 1, 0));
            tiles.Add(new WfcTile("Straight_H", straightColor, 16, 0, 1, 0, 1));

            // 转角
            var cornerColor = new Color(0.56f, 0.50f, 0.44f);
            tiles.Add(new WfcTile("Corner_UR", cornerColor, 12, 1, 1, 0, 0));
            tiles.Add(new WfcTile("Corner_RD", cornerColor, 12, 0, 1, 1, 0));
            tiles.Add(new WfcTile("Corner_DL", cornerColor, 12, 0, 0, 1, 1));
            tiles.Add(new WfcTile("Corner_LU", cornerColor, 12, 1, 0, 0, 1));

            // 死路：只有一个开口。权重给低，否则地图上到处是断头路
            var deadColor = new Color(0.62f, 0.55f, 0.48f);
            tiles.Add(new WfcTile("Dead_U", deadColor, 4, 1, 0, 0, 0));
            tiles.Add(new WfcTile("Dead_R", deadColor, 4, 0, 1, 0, 0));
            tiles.Add(new WfcTile("Dead_D", deadColor, 4, 0, 0, 1, 0));
            tiles.Add(new WfcTile("Dead_L", deadColor, 4, 0, 0, 0, 1));

            return tiles;
        }

        /// <summary>模块是否可通行（房间/通道类），用于撒敌人道具</summary>
        public static bool IsWalkable(WfcTile tile) => tile.Name != "Solid";
    }
}
