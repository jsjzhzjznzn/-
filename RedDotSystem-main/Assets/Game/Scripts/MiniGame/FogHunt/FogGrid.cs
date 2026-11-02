using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>格子状态：未探索 / 已探索但当前不可见 / 当前可见。</summary>
    public enum CellState : byte
    {
        Unknown = 0,
        Explored = 1,
        Visible = 2,
    }

    /// <summary>
    /// 战争迷雾网格：每格记录 explored/visible 两态（见设计文档 §1.4）。
    /// 铁律：离开只把 visible 降级为 explored；判定永远读这张网格，渲染再做平滑。
    /// </summary>
    public sealed class FogGrid
    {
        public readonly int Width;
        public readonly int Height;

        private readonly byte[] _state;
        public readonly bool[] Blocked;
        public readonly bool[] Cover;

        public FogGrid(int width, int height)
        {
            Width = width;
            Height = height;
            _state = new byte[width * height];
            Blocked = new bool[width * height];
            Cover = new bool[width * height];
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        private int Index(int x, int y) => y * Width + x;

        public CellState StateAt(int x, int y) =>
            InBounds(x, y) ? (CellState)_state[Index(x, y)] : CellState.Unknown;

        public bool IsBlocked(int x, int y) => !InBounds(x, y) || Blocked[Index(x, y)];

        public bool IsCover(int x, int y) => InBounds(x, y) && Cover[Index(x, y)];

        public bool IsVisible(int x, int y) => StateAt(x, y) == CellState.Visible;

        public bool IsExplored(int x, int y) => StateAt(x, y) != CellState.Unknown;

        public void SetBlocked(int x, int y, bool value)
        {
            if (InBounds(x, y)) Blocked[Index(x, y)] = value;
        }

        public void SetCover(int x, int y, bool value)
        {
            if (InBounds(x, y)) Cover[Index(x, y)] = value;
        }

        private void MarkVisible(int x, int y)
        {
            if (!InBounds(x, y)) return;
            int i = Index(x, y);
            if (_state[i] < (byte)CellState.Visible) _state[i] = (byte)CellState.Visible;
        }

        /// <summary>每回合开始：把所有 visible 降级为 explored（explored 永不回退）。</summary>
        public void BeginTurn()
        {
            for (int i = 0; i < _state.Length; i++)
                if (_state[i] == (byte)CellState.Visible) _state[i] = (byte)CellState.Explored;
        }

        /// <summary>圆形视野（玩家）。</summary>
        public void RevealCircle(int cx, int cy, int radius)
        {
            int r2 = radius * radius;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (!InBounds(x, y)) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    if (!HasLineOfSight(cx, cy, x, y)) continue;
                    MarkVisible(x, y);
                }
            }
        }

        /// <summary>扇形视野（守卫）。</summary>
        public void RevealCone(int cx, int cy, int radius, float facingX, float facingY, float cosHalfFov)
        {
            int r2 = radius * radius;
            for (int y = cy - radius; y <= cy + radius; y++)
            {
                for (int x = cx - radius; x <= cx + radius; x++)
                {
                    if (!InBounds(x, y)) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    float len = Mathf.Sqrt(dx * dx + dy * dy);
                    if (len > 0.001f)
                    {
                        float dot = (dx * facingX + dy * facingY) / len;
                        if (dot < cosHalfFov) continue;
                    }
                    if (!HasLineOfSight(cx, cy, x, y)) continue;
                    MarkVisible(x, y);
                }
            }
        }

        /// <summary>Bresenham 逐格检查；起点与终点不算阻挡（墙格本身可以看见）。</summary>
        public bool HasLineOfSight(int x0, int y0, int x1, int y1)
        {
            int dx = Mathf.Abs(x1 - x0);
            int dy = Mathf.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;
            int x = x0, y = y0;

            for (int guard = 0; guard < 512; guard++)
            {
                if (x == x1 && y == y1) return true;
                bool isEndpoint = (x == x1 && y == y1);
                bool isOrigin = (x == x0 && y == y0);
                if (!isOrigin && !isEndpoint && IsBlocked(x, y)) return false;

                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y += sy;
                }
            }
            return true;
        }

        /// <summary>调试：全图点亮。</summary>
        public void RevealAll()
        {
            for (int i = 0; i < _state.Length; i++)
                if (_state[i] < (byte)CellState.Explored) _state[i] = (byte)CellState.Explored;
        }
    }
}
