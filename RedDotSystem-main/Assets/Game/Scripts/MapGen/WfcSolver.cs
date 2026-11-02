using System;
using System.Collections.Generic;
using UnityEngine;

namespace Game.MapGen
{
    /// <summary>
    /// 波函数坍塌（Wave Function Collapse）求解器。
    /// 每个格子维护一个「还可能是什么模块」的候选集合，反复挑候选最少（熵最低）的格子把它确定下来，
    /// 同时根据相邻格把不合法的候选剔除；一旦某格候选清空就回溯，撤销上一次选择重新选。
    /// </summary>
    public class WfcSolver
    {
        private readonly int width;
        private readonly int height;
        private readonly List<WfcTile> tiles;
        private readonly System.Random random;

        /// <summary>每格候选：第 x + y*width 个格子的候选位图，第 i 位表示 tiles[i] 可行</summary>
        private int[] wave;
        /// <summary>每格当前候选数量，0 表示该格已确定</summary>
        private int[] entropies;

        /// <summary>邻接规则表：compatible[边][模块A * n + 模块B] = 1 表示 B 可以在 A 的该侧相邻</summary>
        private readonly bool[][] compatible;

        /// <summary>回溯日志：每次做决定时记录「格子 + 该格之前的候选位图」</summary>
        private readonly Stack<int[]> trail = new Stack<int[]>();

        public int Width => width;
        public int Height => height;
        public List<WfcTile> Tiles => tiles;

        /// <summary>
        /// 建区掩码：第 i 位为 0 的格子（水域/山体等不可建区）在初始化时直接定为实心墙，
        /// 不参与坍塌。传 null 表示全图都可建。
        /// </summary>
        private readonly bool[] buildable;
        private int solidIndex;

        public WfcSolver(int width, int height, List<WfcTile> tiles, int seed, bool[] buildable = null)
        {
            this.width = width;
            this.height = height;
            this.tiles = tiles;
            this.buildable = buildable;
            random = new System.Random(seed);

            int n = tiles.Count;
            compatible = new bool[WfcTile.SideCount][];
            for (int side = 0; side < WfcTile.SideCount; side++)
            {
                compatible[side] = new bool[n * n];
                for (int a = 0; a < n; a++)
                {
                    for (int b = 0; b < n; b++)
                    {
                        // a 在 side 方向的开口，必须等于 b 在反方向的开口
                        compatible[side][a * n + b] =
                            tiles[a].EdgeAt(side) == tiles[b].EdgeAt(WfcTile.Opposite(side));
                    }
                }
            }

            wave = new int[width * height];
            entropies = new int[width * height];

            // 实心模块的索引：不可建区要直接填它
            solidIndex = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].Name == "Solid")
                {
                    solidIndex = i;
                    break;
                }
            }

            Reset();
        }

        /// <summary>
        /// 重置为初始状态：所有格子先取全候选，再按「地图外一律封闭」把贴边格子朝外的候选裁掉。
        /// 边界必须在初始化时裁，否则房间类模块（四面通透）永远无法满足贴边约束。
        /// </summary>
        public void Reset()
        {
            int all = (1 << tiles.Count) - 1;
            for (int i = 0; i < wave.Length; i++)
            {
                wave[i] = all;
                entropies[i] = tiles.Count;
            }
            trail.Clear();

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int cell = x + y * width;

                    // 不可建区（水域、山体）直接定为实心，不参与坍塌，也不需要边界裁剪
                    if (buildable != null && !buildable[cell])
                    {
                        SetCell(cell, 1 << solidIndex);
                        continue;
                    }

                    int mask = all;
                    if (y == height - 1) mask &= MaskWithClosedSide(WfcTile.Up);
                    if (x == width - 1) mask &= MaskWithClosedSide(WfcTile.Right);
                    if (y == 0) mask &= MaskWithClosedSide(WfcTile.Down);
                    if (x == 0) mask &= MaskWithClosedSide(WfcTile.Left);
                    SetCell(cell, mask);
                }
            }
        }

        /// <summary>只保留在指定方向没有开口的模块</summary>
        private int MaskWithClosedSide(int side)
        {
            int mask = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].EdgeAt(side) == 0)
                {
                    mask |= 1 << i;
                }
            }
            return mask;
        }

        /// <summary>
        /// 求解整张地图。返回 false 表示在当前尝试次数内没能收敛。
        /// 每次失败都会自动 Reset 重来，避免把半成品地图画出来。
        /// </summary>
        public bool Solve(int maxAttempts, bool allowBacktrack, out int attemptsUsed)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                Reset();
                if (RunOnce(allowBacktrack))
                {
                    attemptsUsed = attempt;
                    return true;
                }
            }
            attemptsUsed = maxAttempts;
            return false;
        }

        private bool RunOnce(bool allowBacktrack)
        {
            int remaining = wave.Length;

            while (remaining > 0)
            {
                int cell = FindLowestEntropyCell();
                if (cell < 0)
                {
                    return false;
                }

                if (entropies[cell] == 0)
                {
                    // 该格候选被清空：回溯或直接判失败
                    if (!allowBacktrack || !Backtrack())
                    {
                        return false;
                    }
                    remaining = CountUndecided();
                    continue;
                }

                if (!Collapse(cell))
                {
                    return false;
                }

                if (!Propagate(cell))
                {
                    if (!allowBacktrack || !Backtrack())
                    {
                        return false;
                    }
                    remaining = CountUndecided();
                    continue;
                }

                remaining = CountUndecided();
            }

            return true;
        }

        /// <summary>挑候选最少的格子；数量相同随机挑，避免生成结果呈网格偏置</summary>
        private int FindLowestEntropyCell()
        {
            int bestCount = int.MaxValue;
            var candidates = new List<int>();

            for (int i = 0; i < entropies.Length; i++)
            {
                int e = entropies[i];
                if (e <= 0 || e == 1)
                {
                    continue;
                }
                if (e < bestCount)
                {
                    bestCount = e;
                    candidates.Clear();
                    candidates.Add(i);
                }
                else if (e == bestCount)
                {
                    candidates.Add(i);
                }
            }

            return candidates.Count == 0 ? -1 : candidates[random.Next(candidates.Count)];
        }

        /// <summary>按权重在候选里挑一个模块定下来</summary>
        private bool Collapse(int cell)
        {
            int mask = wave[cell];
            int totalWeight = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    totalWeight += tiles[i].Weight;
                }
            }
            if (totalWeight <= 0)
            {
                return false;
            }

            int pick = random.Next(totalWeight);
            int chosen = -1;
            for (int i = 0; i < tiles.Count; i++)
            {
                if ((mask & (1 << i)) == 0)
                {
                    continue;
                }
                pick -= tiles[i].Weight;
                if (pick < 0)
                {
                    chosen = i;
                    break;
                }
            }
            if (chosen < 0)
            {
                return false;
            }

            trail.Push(new[] { cell, mask });
            SetCell(cell, 1 << chosen);
            return true;
        }

        /// <summary>把某格的确定结果向四周传播，剔除不合法候选；返回 false 表示出现矛盾</summary>
        private bool Propagate(int startCell)
        {
            var stack = new Stack<int>();
            stack.Push(startCell);

            while (stack.Count > 0)
            {
                int cell = stack.Pop();
                int cx = cell % width;
                int cy = cell / width;
                int mask = wave[cell];

                for (int side = 0; side < WfcTile.SideCount; side++)
                {
                    int nx = cx + (side == WfcTile.Right ? 1 : side == WfcTile.Left ? -1 : 0);
                    int ny = cy + (side == WfcTile.Up ? 1 : side == WfcTile.Down ? -1 : 0);
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        // 越界按「封闭」处理：只有在该侧没有开口的模块才能贴边
                        if (HasAnyOpen(side, mask))
                        {
                            return false;
                        }
                        continue;
                    }

                    int neighbor = nx + ny * width;
                    int neighborMask = wave[neighbor];
                    int allowed = AllowedNeighborMask(side, mask);

                    int newMask = neighborMask & allowed;
                    if (newMask == neighborMask)
                    {
                        continue;
                    }
                    if (newMask == 0)
                    {
                        return false;
                    }

                    SetCell(neighbor, newMask);
                    stack.Push(neighbor);
                }
            }

            return true;
        }

        /// <summary>给定 side 方向与当前候选，求对侧格允许的候选并集</summary>
        private int AllowedNeighborMask(int side, int mask)
        {
            int n = tiles.Count;
            int result = 0;
            bool[] rules = compatible[side];
            for (int a = 0; a < n; a++)
            {
                if ((mask & (1 << a)) == 0)
                {
                    continue;
                }
                for (int b = 0; b < n; b++)
                {
                    if (rules[a * n + b])
                    {
                        result |= 1 << b;
                    }
                }
            }
            return result;
        }

        private bool HasAnyOpen(int side, int mask)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if ((mask & (1 << i)) != 0 && tiles[i].EdgeAt(side) == 1)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>撤销最近一次决定，把该格恢复成上一轮的候选集合</summary>
        private bool Backtrack()
        {
            if (trail.Count == 0)
            {
                return false;
            }

            int[] last = trail.Pop();
            int cell = last[0];
            int oldMask = last[1];

            // 把这一格换成一个更小的候选集合：去掉上一轮选中的那个模块
            int selected = wave[cell];
            int reduced = oldMask & ~selected;
            if (reduced == 0)
            {
                return false;
            }

            // 恢复该格后要重新传播，因此把当前状态整体重算一次
            SetCell(cell, reduced);
            return PropagateAll();
        }

        /// <summary>从所有已确定格子重新传播一遍（回溯后的全局修复）</summary>
        private bool PropagateAll()
        {
            for (int i = 0; i < wave.Length; i++)
            {
                if (entropies[i] == 1 && !Propagate(i))
                {
                    return false;
                }
            }
            return true;
        }

        private void SetCell(int cell, int mask)
        {
            wave[cell] = mask;
            entropies[cell] = CountBits(mask);
        }

        private int CountUndecided()
        {
            int n = 0;
            for (int i = 0; i < entropies.Length; i++)
            {
                if (entropies[i] > 1)
                {
                    n++;
                }
            }
            return n;
        }

        private static int CountBits(int mask)
        {
            int c = 0;
            while (mask != 0)
            {
                mask &= mask - 1;
                c++;
            }
            return c;
        }

        /// <summary>读取某格最终确定的模块索引；未确定返回 -1</summary>
        public int GetTileIndex(int x, int y)
        {
            int i = x + y * width;
            if (entropies[i] != 1)
            {
                return -1;
            }
            for (int t = 0; t < tiles.Count; t++)
            {
                if ((wave[i] & (1 << t)) != 0)
                {
                    return t;
                }
            }
            return -1;
        }
    }
}
