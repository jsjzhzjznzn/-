using System.Collections.Generic;
using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>生成结果：网格 + 房间标记 + 出生点 + 信标点 + 守卫点。</summary>
    public sealed class FogHuntMap
    {
        public FogGrid Grid;
        public bool[] IsRoom;
        public Vector2Int Start;
        public readonly List<Vector2Int> Beacons = new List<Vector2Int>();
        public readonly List<Vector2Int> Guards = new List<Vector2Int>();
    }

    /// <summary>
    /// 房间 + 走廊的程序化地图：保证连通（房间按顺序用 L 形走廊串起来），
    /// 信标/守卫点按与出生点的距离筛选，避免开局贴脸。
    /// </summary>
    public static class FogHuntMapGenerator
    {
        public static FogHuntMap Generate(int width, int height, int seed, int beaconCount, int guardCount)
        {
            var rng = new System.Random(seed);
            var map = new FogHuntMap();
            var grid = new FogGrid(width, height);
            for (int i = 0; i < grid.Blocked.Length; i++) grid.Blocked[i] = true;
            map.Grid = grid;
            map.IsRoom = new bool[width * height];

            var rooms = new List<RectInt>();
            int attempts = 0;
            while (rooms.Count < 14 && attempts < 400)
            {
                attempts++;
                int w = rng.Next(4, 9);
                int h = rng.Next(4, 9);
                int x = rng.Next(1, Mathf.Max(2, width - w - 1));
                int y = rng.Next(1, Mathf.Max(2, height - h - 1));
                var rect = new RectInt(x, y, w, h);

                bool overlap = false;
                for (int i = 0; i < rooms.Count; i++)
                {
                    var other = new RectInt(rooms[i].x - 1, rooms[i].y - 1, rooms[i].width + 2, rooms[i].height + 2);
                    if (rect.Overlaps(other)) { overlap = true; break; }
                }
                if (overlap) continue;

                rooms.Add(rect);
                CarveRect(grid, map.IsRoom, rect);
            }

            for (int i = 1; i < rooms.Count; i++)
                CarveCorridor(grid, Center(rooms[i - 1]), Center(rooms[i]), rng);
            if (rooms.Count > 2) CarveCorridor(grid, Center(rooms[0]), Center(rooms[rooms.Count - 1]), rng);

            map.Start = Center(rooms[0]);

            var floor = new List<Vector2Int>();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (!grid.IsBlocked(x, y)) floor.Add(new Vector2Int(x, y));

            // 草丛：7% 地面格
            for (int i = 0; i < floor.Count; i++)
            {
                if (rng.NextDouble() < 0.07) grid.SetCover(floor[i].x, floor[i].y, true);
            }

            Shuffle(floor, rng);

            // 信标：离出生点 >= 12
            for (int i = 0; i < floor.Count && map.Beacons.Count < beaconCount; i++)
            {
                var cell = floor[i];
                if (Distance(cell, map.Start) < 12) continue;
                if (Contains(map.Beacons, cell)) continue;
                map.Beacons.Add(cell);
            }
            // 保底：放宽到 >= 6
            for (int i = 0; i < floor.Count && map.Beacons.Count < beaconCount; i++)
            {
                var cell = floor[i];
                if (Distance(cell, map.Start) < 6) continue;
                if (Contains(map.Beacons, cell)) continue;
                map.Beacons.Add(cell);
            }

            // 守卫：离出生点 >= 9
            for (int i = 0; i < floor.Count && map.Guards.Count < guardCount; i++)
            {
                var cell = floor[i];
                if (Distance(cell, map.Start) < 9) continue;
                if (Contains(map.Beacons, cell) || Contains(map.Guards, cell)) continue;
                map.Guards.Add(cell);
            }
            for (int i = 0; i < floor.Count && map.Guards.Count < guardCount; i++)
            {
                var cell = floor[i];
                if (Distance(cell, map.Start) < 5) continue;
                if (Contains(map.Beacons, cell) || Contains(map.Guards, cell)) continue;
                map.Guards.Add(cell);
            }

            return map;
        }

        private static void CarveRect(FogGrid grid, bool[] isRoom, RectInt rect)
        {
            for (int y = rect.y; y < rect.y + rect.height; y++)
            for (int x = rect.x; x < rect.x + rect.width; x++)
            {
                grid.SetBlocked(x, y, false);
                isRoom[y * grid.Width + x] = true;
            }
        }

        private static void CarveCorridor(FogGrid grid, Vector2Int a, Vector2Int b, System.Random rng)
        {
            bool horizontalFirst = rng.Next(0, 2) == 0;
            if (horizontalFirst)
            {
                CarveLine(grid, a, new Vector2Int(b.x, a.y));
                CarveLine(grid, new Vector2Int(b.x, a.y), b);
            }
            else
            {
                CarveLine(grid, a, new Vector2Int(a.x, b.y));
                CarveLine(grid, new Vector2Int(a.x, b.y), b);
            }
        }

        private static void CarveLine(FogGrid grid, Vector2Int from, Vector2Int to)
        {
            int dx = to.x >= from.x ? 1 : -1;
            int dy = to.y >= from.y ? 1 : -1;
            int x = from.x, y = from.y;
            int guard = 0;
            while ((x != to.x || y != to.y) && guard++ < 512)
            {
                grid.SetBlocked(x, y, false);
                if (x != to.x) x += dx;
                else if (y != to.y) y += dy;
            }
            grid.SetBlocked(to.x, to.y, false);
        }

        private static Vector2Int Center(RectInt rect) =>
            new Vector2Int(rect.x + rect.width / 2, rect.y + rect.height / 2);

        private static int Distance(Vector2Int a, Vector2Int b) =>
            Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

        private static bool Contains(List<Vector2Int> list, Vector2Int cell)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == cell) return true;
            return false;
        }

        private static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
