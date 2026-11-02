using System.Collections.Generic;
using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 小地图：只画已探索区域，守卫永不出现。
    /// 承载「记住走过的路」这条迷雾规则，是玩家回路线的唯一依据。
    /// </summary>
    public sealed class FogHuntMinimap
    {
        private const int Cell = 4;

        private static readonly Color32 Unknown = new Color32(10, 13, 18, 255);
        private static readonly Color32 Wall = new Color32(46, 50, 60, 255);
        private static readonly Color32 Floor = new Color32(104, 98, 86, 255);
        private static readonly Color32 Dirt = new Color32(88, 82, 70, 255);
        private static readonly Color32 Cover = new Color32(62, 80, 52, 255);
        private static readonly Color32 PlayerColor = new Color32(92, 220, 255, 255);
        private static readonly Color32 ExitColor = new Color32(140, 255, 170, 255);
        private static readonly Color32 BeaconColor = new Color32(255, 206, 74, 255);
        private static readonly Color32 BeaconTaken = new Color32(96, 90, 76, 255);

        private FogGrid _grid;
        private bool[] _isRoom;
        private Texture2D _texture;
        private Color32[] _pixels;
        private int _tw;
        private int _th;

        public Texture2D Texture => _texture;

        public void Build(FogGrid grid, bool[] isRoom)
        {
            _grid = grid;
            _isRoom = isRoom;
            _tw = grid.Width * Cell;
            _th = grid.Height * Cell;
            _pixels = new Color32[_tw * _th];
            _texture = new Texture2D(_tw, _th, TextureFormat.RGBA32, false)
            {
                name = "FogHuntMinimap",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        public void Refresh(Vector2Int player, Vector2Int exit, IList<Vector2Int> beacons,
            IList<bool> taken, IList<bool> seen)
        {
            if (_texture == null) return;

            int w = _grid.Width, h = _grid.Height;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var state = _grid.StateAt(x, y);
                    if (state == CellState.Unknown)
                    {
                        Block(x, y, Unknown);
                        continue;
                    }

                    int index = y * w + x;
                    Color32 color = _grid.Blocked[index]
                        ? Wall
                        : _grid.Cover[index]
                            ? Cover
                            : (_isRoom != null && _isRoom[index] ? Floor : Dirt);

                    if (state == CellState.Visible) color = Brighten(color, 1.35f);
                    Block(x, y, color);
                }
            }

            Block(exit.x, exit.y, ExitColor);

            if (beacons != null && taken != null)
            {
                for (int i = 0; i < beacons.Count && i < taken.Count; i++)
                {
                    bool marked = taken[i] || (seen != null && i < seen.Count && seen[i]);
                    if (!marked) continue;
                    Block(beacons[i].x, beacons[i].y, taken[i] ? BeaconTaken : BeaconColor);
                }
            }

            Block(player.x, player.y, PlayerColor);

            _texture.SetPixels32(_pixels);
            _texture.Apply(false, false);
        }

        private void Block(int cx, int cy, Color32 color)
        {
            int x0 = cx * Cell;
            int y0 = cy * Cell;
            for (int y = 0; y < Cell; y++)
            {
                int row = (y0 + y) * _tw + x0;
                for (int x = 0; x < Cell; x++) _pixels[row + x] = color;
            }
        }

        private static Color32 Brighten(Color32 color, float factor)
        {
            return new Color32(
                (byte)Mathf.Clamp(color.r * factor, 0f, 255f),
                (byte)Mathf.Clamp(color.g * factor, 0f, 255f),
                (byte)Mathf.Clamp(color.b * factor, 0f, 255f),
                255);
        }

        public void Destroy()
        {
            if (_texture != null) Object.Destroy(_texture);
            _texture = null;
        }
    }
}
