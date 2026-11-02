using UnityEngine;

namespace Game.MiniGame.FogHunt
{
    /// <summary>运行时生成的小图标与光效贴图（1 单位大小），不依赖任何美术资源。</summary>
    public static class FogHuntArt
    {
        public static Sprite Circle(Color color, int size = 24) => Build(size, (x, y, cx, cy, r) =>
            (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r, color);

        public static Sprite Triangle(Color color, int size = 24) => Build(size, (x, y, cx, cy, r) =>
        {
            float half = Mathf.Lerp(0f, r, y / (float)(size - 1));
            return Mathf.Abs(x - cx) <= half;
        }, color);

        public static Sprite Diamond(Color color, int size = 24) => Build(size, (x, y, cx, cy, r) =>
            Mathf.Abs(x - cx) + Mathf.Abs(y - cy) <= r, color);

        public static Sprite Ring(Color color, int size = 24) => Build(size, (x, y, cx, cy, r) =>
        {
            float d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            return d2 <= r * r && d2 >= (r - 3) * (r - 3);
        }, color);

        public static Sprite Square(Color color, int size = 24) => Build(size, (x, y, cx, cy, r) =>
            Mathf.Abs(x - cx) <= r - 3 && Mathf.Abs(y - cy) <= r - 3, color);

        /// <summary>朝 +X 的扇形（守卫视野锥），halfFovCos = cos(半角)；外缘与两侧都做软化。</summary>
        public static Sprite Cone(Color color, float halfFovCos = 0.5f, int size = 128) =>
            BuildAlpha(size, size, (dx, dy, rx, ry) =>
            {
                float len = Mathf.Sqrt(dx * dx + dy * dy);
                if (len < 0.001f) return 1f;
                float radial = Mathf.Clamp01(1f - len / rx);
                float angular = Mathf.Clamp01((dx / len - halfFovCos) / 0.18f);
                return radial * angular;
            }, color);

        /// <summary>径向渐变：中心不透明、边缘透明，用作提灯光晕。</summary>
        public static Sprite Radial(Color color, int size = 128) =>
            BuildAlpha(size, size, (dx, dy, rx, ry) =>
            {
                float d = Mathf.Sqrt((dx / rx) * (dx / rx) + (dy / ry) * (dy / ry));
                float a = Mathf.Clamp01(1f - d);
                return a * a;
            }, color);

        /// <summary>软椭圆：单位脚底阴影。</summary>
        public static Sprite SoftEllipse(Color color, int size = 48) =>
            BuildAlpha(size, size / 2, (dx, dy, rx, ry) =>
            {
                float d = Mathf.Sqrt((dx / rx) * (dx / rx) + (dy / ry) * (dy / ry));
                return Mathf.Pow(Mathf.Clamp01(1f - d), 1.4f);
            }, color);

        /// <summary>竖直渐隐光柱（信标）。</summary>
        public static Sprite Pillar(Color color, int width = 32, int height = 128) =>
            BuildAlpha(width, height, (dx, dy, rx, ry) =>
            {
                float nx = Mathf.Abs(dx) / rx;
                float ny = (dy + ry) / (2f * ry);
                return Mathf.Clamp01(1f - nx) * Mathf.Pow(Mathf.Clamp01(ny), 1.4f);
            }, color);

        /// <summary>圆环描边（评级印章、撤离点）。</summary>
        public static Sprite RingOutline(Color color, int size = 128, float thickness = 6f) =>
            BuildAlpha(size, size, (dx, dy, rx, ry) =>
            {
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float inner = rx - thickness;
                return Mathf.Clamp01((rx - d) / 1.5f) * Mathf.Clamp01((d - inner) / 1.5f);
            }, color);

        private delegate bool Picker(int x, int y, float cx, float cy, float r);

        private delegate float AlphaFn(float dx, float dy, float rx, float ry);

        private static Sprite Build(int size, Picker picker, Color color)
        {
            var tex = NewTexture(size, size);
            var pixels = new Color32[size * size];
            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float r = size * 0.5f - 1f;
            var rgb = (Color32)color;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = picker(x, y, cx, cy, r) ? rgb : new Color32(0, 0, 0, 0);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size,
                0, SpriteMeshType.FullRect);
        }

        private static Sprite BuildAlpha(int width, int height, AlphaFn alpha, Color color)
        {
            var tex = NewTexture(width, height);
            var pixels = new Color32[width * height];
            float cx = (width - 1) * 0.5f;
            float cy = (height - 1) * 0.5f;
            float rx = width * 0.5f - 0.5f;
            float ry = height * 0.5f - 0.5f;
            var rgb = (Color32)color;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float a = Mathf.Clamp01(alpha(x - cx, y - cy, rx, ry));
                    pixels[y * width + x] = new Color32(rgb.r, rgb.g, rgb.b,
                        (byte)Mathf.RoundToInt(a * color.a * 255f));
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), Mathf.Max(width, height),
                0, SpriteMeshType.FullRect);
        }

        private static Texture2D NewTexture(int width, int height)
        {
            return new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }
    }
}
