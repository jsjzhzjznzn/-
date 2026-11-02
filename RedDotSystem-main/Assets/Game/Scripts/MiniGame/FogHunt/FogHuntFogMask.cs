using System.Collections.Generic;
using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 迷雾遮罩：每格 4 像素，按「到可见区的距离」生成柔边，再模糊两次，最后双线性放大成一层雾。
    /// 只读 FogGrid 的三态，不做任何判定。
    /// </summary>
    public sealed class FogHuntFogMask
    {
        private const int Cell = 4;

        private static readonly Color32 UnknownColor = new Color32(5, 7, 11, 255);
        private static readonly Color32 ExploredColor = new Color32(11, 19, 33, 255);

        private FogGrid _grid;
        private Texture2D _texture;
        private SpriteRenderer _renderer;

        private int[] _distVisible;
        private int[] _distExplored;
        private Color32[] _cellColor;
        private float[] _alpha;
        private float[] _scratch;
        private Color32[] _pixels;
        private int _tw;
        private int _th;
        private readonly Queue<int> _queue = new Queue<int>();

        public void Build(FogGrid grid, Transform parent)
        {
            _grid = grid;
            _tw = grid.Width * Cell;
            _th = grid.Height * Cell;
            _pixels = new Color32[_tw * _th];
            _alpha = new float[_tw * _th];
            _scratch = new float[_tw * _th];
            _distVisible = new int[grid.Width * grid.Height];
            _distExplored = new int[grid.Width * grid.Height];
            _cellColor = new Color32[grid.Width * grid.Height];

            _texture = new Texture2D(_tw, _th, TextureFormat.RGBA32, false)
            {
                name = "FogHuntMask",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var go = new GameObject("FogHuntMask");
            go.transform.SetParent(parent, false);
            _renderer = go.AddComponent<SpriteRenderer>();
            _renderer.sprite = Sprite.Create(_texture, new Rect(0, 0, _tw, _th), Vector2.zero, Cell, 0,
                SpriteMeshType.FullRect);
            _renderer.sortingOrder = 40;
        }

        public void Refresh(Vector2Int player)
        {
            if (_texture == null) return;

            int w = _grid.Width, h = _grid.Height;
            Bfs(_distVisible, false);
            Bfs(_distExplored, true);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int ci = y * w + x;
                    var state = _grid.StateAt(x, y);
                    float alpha;

                    if (state == CellState.Visible)
                    {
                        float dx = x - player.x, dy = y - player.y;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        alpha = 0.02f + 0.10f * SmoothStep(3.4f, 6.4f, d);
                        _cellColor[ci] = ExploredColor;
                    }
                    else if (state == CellState.Explored)
                    {
                        alpha = 0.14f + 0.41f * SmoothStep(0f, 2f, _distVisible[ci]);
                        _cellColor[ci] = ExploredColor;
                    }
                    else
                    {
                        alpha = 0.62f + 0.38f * SmoothStep(0f, 1.5f, _distExplored[ci]);
                        _cellColor[ci] = UnknownColor;
                    }

                    for (int py = 0; py < Cell; py++)
                    {
                        for (int px = 0; px < Cell; px++)
                        {
                            _alpha[(y * Cell + py) * _tw + (x * Cell + px)] = alpha;
                        }
                    }
                }
            }

            Blur();
            Blur();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var color = _cellColor[y * w + x];
                    for (int py = 0; py < Cell; py++)
                    {
                        for (int px = 0; px < Cell; px++)
                        {
                            int ti = (y * Cell + py) * _tw + (x * Cell + px);
                            _pixels[ti] = new Color32(color.r, color.g, color.b,
                                (byte)Mathf.RoundToInt(Mathf.Clamp01(_alpha[ti]) * 255f));
                        }
                    }
                }
            }

            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
        }

        /// <summary>多源 BFS：fromNonUnknown = true 时以所有已探索格为源，否则只以当前可见格为源。</summary>
        private void Bfs(int[] dist, bool fromNonUnknown)
        {
            int w = _grid.Width, h = _grid.Height;
            _queue.Clear();
            for (int i = 0; i < dist.Length; i++)
            {
                var state = _grid.StateAt(i % w, i / w);
                bool source = fromNonUnknown ? state != CellState.Unknown : state == CellState.Visible;
                dist[i] = source ? 0 : int.MaxValue;
                if (source) _queue.Enqueue(i);
            }

            while (_queue.Count > 0)
            {
                int index = _queue.Dequeue();
                int x = index % w;
                int y = index / w;
                int next = dist[index] + 1;

                if (x > 0) Push(dist, index - 1, next);
                if (x < w - 1) Push(dist, index + 1, next);
                if (y > 0) Push(dist, index - w, next);
                if (y < h - 1) Push(dist, index + w, next);
            }
        }

        private void Push(int[] dist, int index, int value)
        {
            if (dist[index] <= value) return;
            dist[index] = value;
            _queue.Enqueue(index);
        }

        private void Blur()
        {
            for (int y = 0; y < _th; y++)
            {
                for (int x = 0; x < _tw; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int sy = y + dy;
                        if (sy < 0 || sy >= _th) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= _tw) continue;
                            sum += _alpha[sy * _tw + sx];
                            count++;
                        }
                    }
                    _scratch[y * _tw + x] = sum / count;
                }
            }

            var swap = _alpha;
            _alpha = _scratch;
            _scratch = swap;
        }

        private static float SmoothStep(float edge0, float edge1, float value)
        {
            if (edge1 <= edge0) return value >= edge1 ? 1f : 0f;
            float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        public void Destroy()
        {
            if (_texture != null) Object.Destroy(_texture);
            if (_renderer != null) Object.Destroy(_renderer.gameObject);
            _texture = null;
            _renderer = null;
        }
    }
}
