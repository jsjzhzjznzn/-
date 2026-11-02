using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGen
{
    /// <summary>房间语义：决定配色与后续玩法用途</summary>
    public enum RoguelikeRoomType
    {
        Start,    // 起始房
        Normal,   // 战斗房
        Treasure, // 宝箱房
        Shop,     // 商店房
        Boss,     // BOSS 房
    }

    /// <summary>一个矩形房间</summary>
    public class RoguelikeRoom
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public RoguelikeRoomType Type = RoguelikeRoomType.Normal;

        public int CenterX => X + Width / 2;
        public int CenterY => Y + Height / 2;

        public bool Contains(int x, int y)
        {
            return x >= X && y >= Y && x < X + Width && y < Y + Height;
        }
    }

    /// <summary>生成结果：地面掩码、房门掩码、每个格子归属的房间下标</summary>
    public class RoguelikeResult
    {
        public int Width;
        public int Height;
        /// <summary>可通行地面</summary>
        public bool[] Floor;
        /// <summary>房门（走廊与房间交界的那一圈格子）</summary>
        public bool[] Door;
        /// <summary>格子归属的房间下标，走廊为 -1</summary>
        public int[] RoomIndex;
        public List<RoguelikeRoom> Rooms = new List<RoguelikeRoom>();
        public List<int[]> Links = new List<int[]>();
        public int StartRoomIndex;
        public int BossRoomIndex;

        public bool IsFloor(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height && Floor[x + y * Width];
        }

        public bool IsDoor(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height && Door[x + y * Width];
        }

        public int RoomAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return -1;
            return RoomIndex[x + y * Width];
        }

        /// <summary>房间图上的拓扑距离（跳数），用于判定 BOSS 房</summary>
        public int[] RoomHops()
        {
            int n = Rooms.Count;
            var hops = new int[n];
            for (int i = 0; i < n; i++) hops[i] = -1;

            var adjacency = new List<int>[n];
            for (int i = 0; i < n; i++) adjacency[i] = new List<int>();
            foreach (var link in Links)
            {
                adjacency[link[0]].Add(link[1]);
                adjacency[link[1]].Add(link[0]);
            }

            var queue = new Queue<int>();
            hops[StartRoomIndex] = 0;
            queue.Enqueue(StartRoomIndex);
            while (queue.Count > 0)
            {
                int cur = queue.Dequeue();
                foreach (int next in adjacency[cur])
                {
                    if (hops[next] >= 0) continue;
                    hops[next] = hops[cur] + 1;
                    queue.Enqueue(next);
                }
            }
            return hops;
        }
    }

    /// <summary>
    /// 元气骑士式地牢生成：格子槽里放矩形房间（槽位天然不重叠），
    /// 用最小生成树把房间连成一棵连通树，再加几条额外边形成回路，最后凿 L 形走廊。
    /// </summary>
    public static class RoguelikeGenerator
    {
        public static RoguelikeResult Generate(
            int width,
            int height,
            int slotCols,
            int slotRows,
            int seed,
            float roomSkipChance = 0.28f,
            int extraLinks = 3,
            int corridorWidth = 2)
        {
            var rng = new System.Random(seed);
            var result = new RoguelikeResult
            {
                Width = width,
                Height = height,
                Floor = new bool[width * height],
                Door = new bool[width * height],
                RoomIndex = new int[width * height],
            };
            for (int i = 0; i < result.RoomIndex.Length; i++) result.RoomIndex[i] = -1;

            PlaceRooms(result, rng, width, height, slotCols, slotRows, roomSkipChance);
            if (result.Rooms.Count < 2)
            {
                return result;
            }

            FillRoomFloor(result);
            BuildLinks(result, rng, extraLinks);
            CarveCorridors(result, rng, corridorWidth);
            MarkDoors(result);
            AssignRoomTypes(result, rng);

            return result;
        }

        /// <summary>每个槽位内放一个随机尺寸的矩形房间，槽位保证互不重叠</summary>
        private static void PlaceRooms(RoguelikeResult result, System.Random rng,
            int width, int height, int slotCols, int slotRows, float roomSkipChance)
        {
            int slotW = width / slotCols;
            int slotH = height / slotRows;

            for (int row = 0; row < slotRows; row++)
            {
                for (int col = 0; col < slotCols; col++)
                {
                    if (rng.NextDouble() < roomSkipChance)
                    {
                        continue;
                    }

                    int minW = Mathf.Max(4, Mathf.RoundToInt(slotW * 0.45f));
                    int maxW = Mathf.Max(minW, Mathf.RoundToInt(slotW * 0.80f));
                    int minH = Mathf.Max(4, Mathf.RoundToInt(slotH * 0.45f));
                    int maxH = Mathf.Max(minH, Mathf.RoundToInt(slotH * 0.80f));

                    int rw = rng.Next(minW, maxW + 1);
                    int rh = rng.Next(minH, maxH + 1);

                    int slotX = col * slotW;
                    int slotY = row * slotH;
                    int maxOffsetX = Mathf.Max(1, slotW - rw - 1);
                    int maxOffsetY = Mathf.Max(1, slotH - rh - 1);

                    int ox = slotX + rng.Next(1, maxOffsetX + 1);
                    int oy = slotY + rng.Next(1, maxOffsetY + 1);
                    ox = Mathf.Clamp(ox, 1, Mathf.Max(1, width - rw - 1));
                    oy = Mathf.Clamp(oy, 1, Mathf.Max(1, height - rh - 1));

                    result.Rooms.Add(new RoguelikeRoom { X = ox, Y = oy, Width = rw, Height = rh });
                }
            }
        }

        private static void FillRoomFloor(RoguelikeResult result)
        {
            for (int i = 0; i < result.Rooms.Count; i++)
            {
                var room = result.Rooms[i];
                for (int y = room.Y; y < room.Y + room.Height; y++)
                {
                    for (int x = room.X; x < room.X + room.Width; x++)
                    {
                        if (x < 0 || y < 0 || x >= result.Width || y >= result.Height) continue;
                        int idx = x + y * result.Width;
                        result.Floor[idx] = true;
                        result.RoomIndex[idx] = i;
                    }
                }
            }
        }

        /// <summary>最小生成树连接所有房间，再加 extraLinks 条额外边制造回路</summary>
        private static void BuildLinks(RoguelikeResult result, System.Random rng, int extraLinks)
        {
            int n = result.Rooms.Count;
            var inTree = new bool[n];
            inTree[0] = true;

            for (int step = 1; step < n; step++)
            {
                int bestA = -1, bestB = -1;
                float bestDistance = float.MaxValue;

                for (int a = 0; a < n; a++)
                {
                    if (!inTree[a]) continue;
                    for (int b = 0; b < n; b++)
                    {
                        if (inTree[b]) continue;
                        float d = Distance(result.Rooms[a], result.Rooms[b]);
                        // 加一点随机扰动，避免每次生成都长得一样
                        d *= 1f + (float)rng.NextDouble() * 0.5f;
                        if (d < bestDistance)
                        {
                            bestDistance = d;
                            bestA = a;
                            bestB = b;
                        }
                    }
                }

                if (bestA < 0 || bestB < 0) break;
                inTree[bestB] = true;
                result.Links.Add(new[] { bestA, bestB });
            }

            // 额外连几条，让地图不是一棵死板的树
            for (int i = 0; i < extraLinks && n >= 3; i++)
            {
                int a = rng.Next(n);
                int b = rng.Next(n);
                if (a == b || HasLink(result, a, b)) continue;
                result.Links.Add(new[] { a, b });
            }
        }

        private static bool HasLink(RoguelikeResult result, int a, int b)
        {
            foreach (var link in result.Links)
            {
                if ((link[0] == a && link[1] == b) || (link[0] == b && link[1] == a)) return true;
            }
            return false;
        }

        private static float Distance(RoguelikeRoom a, RoguelikeRoom b)
        {
            float dx = a.CenterX - b.CenterX;
            float dy = a.CenterY - b.CenterY;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>沿房间中心凿 L 形走廊</summary>
        private static void CarveCorridors(RoguelikeResult result, System.Random rng, int corridorWidth)
        {
            foreach (var link in result.Links)
            {
                var a = result.Rooms[link[0]];
                var b = result.Rooms[link[1]];

                if (rng.NextDouble() < 0.5)
                {
                    CarveHorizontal(result, a.CenterX, b.CenterX, a.CenterY, corridorWidth);
                    CarveVertical(result, a.CenterY, b.CenterY, b.CenterX, corridorWidth);
                }
                else
                {
                    CarveVertical(result, a.CenterY, b.CenterY, a.CenterX, corridorWidth);
                    CarveHorizontal(result, a.CenterX, b.CenterX, b.CenterY, corridorWidth);
                }
            }
        }

        private static void CarveHorizontal(RoguelikeResult result, int x0, int x1, int y, int width)
        {
            if (x0 > x1) { int t = x0; x0 = x1; x1 = t; }
            for (int x = x0; x <= x1; x++)
            {
                for (int k = 0; k < width; k++)
                {
                    SetFloor(result, x, y + k);
                }
            }
        }

        private static void CarveVertical(RoguelikeResult result, int y0, int y1, int x, int width)
        {
            if (y0 > y1) { int t = y0; y0 = y1; y1 = t; }
            for (int y = y0; y <= y1; y++)
            {
                for (int k = 0; k < width; k++)
                {
                    SetFloor(result, x + k, y);
                }
            }
        }

        private static void SetFloor(RoguelikeResult result, int x, int y)
        {
            if (x < 0 || y < 0 || x >= result.Width || y >= result.Height) return;
            result.Floor[x + y * result.Width] = true;
        }

        /// <summary>房间外一圈里凡是地面的格子，就是走廊进入房间的门口</summary>
        private static void MarkDoors(RoguelikeResult result)
        {
            foreach (var room in result.Rooms)
            {
                for (int x = room.X - 1; x <= room.X + room.Width; x++)
                {
                    MarkDoorIfFloor(result, x, room.Y - 1);
                    MarkDoorIfFloor(result, x, room.Y + room.Height);
                }
                for (int y = room.Y - 1; y <= room.Y + room.Height; y++)
                {
                    MarkDoorIfFloor(result, room.X - 1, y);
                    MarkDoorIfFloor(result, room.X + room.Width, y);
                }
            }
        }

        private static void MarkDoorIfFloor(RoguelikeResult result, int x, int y)
        {
            if (!result.IsFloor(x, y)) return;
            result.Door[x + y * result.Width] = true;
        }

        /// <summary>起始房取最靠左下的房间，BOSS 房取拓扑距离最远者，宝箱/商店随机分派</summary>
        private static void AssignRoomTypes(RoguelikeResult result, System.Random rng)
        {
            int n = result.Rooms.Count;
            if (n == 0) return;

            int start = 0;
            for (int i = 1; i < n; i++)
            {
                var a = result.Rooms[i];
                var cur = result.Rooms[start];
                if (a.CenterX + a.CenterY < cur.CenterX + cur.CenterY)
                {
                    start = i;
                }
            }
            result.Rooms[start].Type = RoguelikeRoomType.Start;
            result.StartRoomIndex = start;

            int[] hops = result.RoomHops();
            int boss = start;
            int bestHop = -1;
            for (int i = 0; i < n; i++)
            {
                if (i == start) continue;
                if (hops[i] > bestHop)
                {
                    bestHop = hops[i];
                    boss = i;
                }
            }
            result.Rooms[boss].Type = RoguelikeRoomType.Boss;
            result.BossRoomIndex = boss;

            // 剩下的房间随机挑两个当宝箱房与商店房
            var candidates = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if (i != start && i != boss) candidates.Add(i);
            }
            if (candidates.Count > 0)
            {
                int pick = candidates[rng.Next(candidates.Count)];
                result.Rooms[pick].Type = RoguelikeRoomType.Treasure;
                candidates.Remove(pick);
            }
            if (candidates.Count > 0)
            {
                int pick = candidates[rng.Next(candidates.Count)];
                result.Rooms[pick].Type = RoguelikeRoomType.Shop;
            }
        }
    }
}