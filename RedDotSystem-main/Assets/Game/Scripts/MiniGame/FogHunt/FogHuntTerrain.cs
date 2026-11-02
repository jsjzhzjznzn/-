using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>
    /// 静态地形贴图：每格 32 像素，房间石板 / 走廊泥土 / 2.5D 墙面 / 草丛 / 墙脚 AO。
    /// 只在进场景时生成一次，之后不再重算；迷雾的明暗完全交给 FogHuntFogMask。
    /// </summary>
    public static class FogHuntTerrain
    {
        public const int CellSize = 32;

        private static readonly Color32 Stone = new Color32(148, 141, 124, 255);
        private static readonly Color32 StoneGrout = new Color32(108, 102, 89, 255);
        private static readonly Color32 Dirt = new Color32(94, 86, 71, 255);
        private static readonly Color32 GrassBase = new Color32(68, 92, 56, 255);
        private static readonly Color32 GrassBlade = new Color32(112, 146, 86, 255);
        private static readonly Color32 WallBase = new Color32(56, 60, 72, 255);
        private static readonly Color32 WallTop = new Color32(88, 94, 110, 255);
        private static readonly Color32 WallBottom = new Color32(34, 37, 46, 255);

        public static Texture2D Build(FogGrid grid, bool[] isRoom, int seed)
        {
            int w = grid.Width, h = grid.Height, c = CellSize;
            int tw = w * c, th = h * c;
            var pixels = new Color32[tw * th];
            var rng = new System.Random(seed * 7919 + 131);
            var blades = new int[c];

            for (int cy = 0; cy < h; cy++)
            {
                for (int cx = 0; cx < w; cx++)
                {
                    int index = cy * w + cx;
                    bool wall = grid.Blocked[index];
                    bool room = isRoom != null && index < isRoom.Length && isRoom[index];
                    bool cover = !wall && grid.Cover[index];
                    float ao = wall ? 1f : WallAo(grid, cx, cy);
                    float tile = 0.94f + (float)rng.NextDouble() * 0.12f;

                    for (int i = 0; i < c; i++) blades[i] = 0;
                    if (cover)
                    {
                        int count = 2 + rng.Next(3);
                        for (int b = 0; b < count; b++)
                        {
                            int x = rng.Next(2, c - 3);
                            int height = rng.Next(c / 4, c / 2);
                            blades[x] = height;
                            blades[x + 1] = Mathf.Max(1, height - 3);
                        }
                    }

                    for (int py = 0; py < c; py++)
                    {
                        for (int px = 0; px < c; px++)
                        {
                            Color32 col = wall
                                ? WallPixel(px, py, tile, c, rng)
                                : room
                                    ? StonePixel(px, py, tile, c, ao, rng)
                                    : DirtPixel(px, py, tile, ao, rng);

                            if (cover)
                            {
                                col = Blend(col, GrassBase, 0.5f);
                                if (py < blades[px]) col = Mul(GrassBlade, tile * ao);
                            }

                            pixels[(cy * c + py) * tw + (cx * c + px)] = col;
                        }
                    }
                }
            }

            var texture = new Texture2D(tw, th, TextureFormat.RGBA32, false)
            {
                name = "FogHuntTerrain",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static float WallAo(FogGrid grid, int cx, int cy)
        {
            bool near = grid.IsBlocked(cx - 1, cy) || grid.IsBlocked(cx + 1, cy) ||
                        grid.IsBlocked(cx, cy - 1) || grid.IsBlocked(cx, cy + 1);
            return near ? 0.86f : 1f;
        }

        /// <summary>房间石板：左/下各 2 像素灰缝，表面颗粒。</summary>
        private static Color32 StonePixel(int px, int py, float tile, int c, float ao, System.Random rng)
        {
            float grain = 0.96f + (float)rng.NextDouble() * 0.08f;
            bool grout = px < 2 || py < 2;
            return Mul(grout ? StoneGrout : Stone, tile * grain * ao);
        }

        /// <summary>走廊泥土：高频颗粒，没有砖缝。</summary>
        private static Color32 DirtPixel(int px, int py, float tile, float ao, System.Random rng)
        {
            float grain = 0.86f + (float)rng.NextDouble() * 0.28f;
            return Mul(Dirt, tile * grain * ao);
        }

        /// <summary>墙：顶亮边 + 底暗边 + 每块左缘压暗，读起来有高度。</summary>
        private static Color32 WallPixel(int px, int py, float tile, int c, System.Random rng)
        {
            float grain = 0.94f + (float)rng.NextDouble() * 0.12f;
            float f = tile * grain;
            int topBand = Mathf.Max(3, Mathf.RoundToInt(c * 0.18f));
            int bottomBand = Mathf.Max(3, Mathf.RoundToInt(c * 0.12f));

            if (py >= c - topBand) return Mul(WallTop, f);
            if (py < bottomBand) return Mul(WallBottom, f * 0.92f);
            if (px < 2) return Mul(WallBase, f * 0.88f);
            return Mul(WallBase, f);
        }

        private static Color32 Mul(Color32 color, float factor)
        {
            return new Color32(
                (byte)Mathf.Clamp(color.r * factor, 0f, 255f),
                (byte)Mathf.Clamp(color.g * factor, 0f, 255f),
                (byte)Mathf.Clamp(color.b * factor, 0f, 255f),
                255);
        }

        private static Color32 Blend(Color32 a, Color32 b, float t)
        {
            return new Color32(
                (byte)Mathf.Lerp(a.r, b.r, t),
                (byte)Mathf.Lerp(a.g, b.g, t),
                (byte)Mathf.Lerp(a.b, b.b, t),
                255);
        }
    }
}
