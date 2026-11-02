#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 影像簿页的程序化美术：圆角底板 / 金圆环 / 细线 / 柔光 / 各种小图标。
/// 全部白色绘制、靠 Image.color 染色；统一 FullRect 导入（低 alpha 渐变图用 Tight 会被裁没）。
/// </summary>
internal static class ProfileArt
{
    /// <summary>
    /// 补齐缺失的程序化美术。
    /// 已存在的一律跳过：每轮重建都重写+重导 16 张图会把编辑器的 GfxDevice 资源 ID 表（20 位）打爆，
    /// 之后整个进程都在刷 "Resource ID out of range"。要强制重生成就删掉对应的 .png 再重建。
    /// </summary>
    public static void EnsureAll(string dir)
    {
        EnsureFolder(dir);
        EnsureMask(dir, "profile_panel", 128, 128, RoundedRect(128, 128, 30f), new Vector4(32, 32, 32, 32), 128);
        // 卡片框是「描边环」，不是实心圆角块：实心图会给整张卡刷上一层金
        EnsureMask(dir, "card_frame", 128, 192, RoundedRing(128, 192, 26f, 2.5f), new Vector4(30, 30, 30, 30), 256);
        EnsureMask(dir, "gold_ring", 256, 256, Ring, Vector4.zero, 256);
        EnsureMask(dir, "gold_ring_dots", 256, 256, RingDots, Vector4.zero, 256);
        EnsureMask(dir, "gold_line", 4, 4, (u, v) => true, Vector4.zero, 4);
        EnsureGlow(dir, "soft_glow", 256);
        EnsureVignette(dir, "vignette", 256);
        EnsureMask(dir, "title_mark", 64, 64, TitleMark, Vector4.zero, 64);
        EnsureMask(dir, "crosshair", 64, 64, Crosshair, Vector4.zero, 64);
        EnsureMask(dir, "icon_dots", 64, 64, Dots, Vector4.zero, 64);
        EnsureMask(dir, "icon_pen", 64, 64, Pen, Vector4.zero, 64);
        EnsureMask(dir, "icon_copy", 64, 64, Copy, Vector4.zero, 64);
        EnsureMask(dir, "icon_info", 64, 64, Info, Vector4.zero, 64);
        EnsureMask(dir, "icon_eye", 64, 64, Eye, Vector4.zero, 64);
        EnsureMask(dir, "icon_play", 64, 64, Play, Vector4.zero, 64);
    }

    private static bool Exists(string dir, string name)
        => AssetDatabase.LoadAssetAtPath<Sprite>($"{dir}/{name}.png") != null;

    private static void EnsureMask(string dir, string name, int w, int h,
        Func<float, float, bool> inside, Vector4 border, int maxSize)
    {
        if (Exists(dir, name)) return;
        SaveMask($"{dir}/{name}.png", w, h, inside, border, maxSize);
    }

    private static void EnsureGlow(string dir, string name, int size)
    {
        if (Exists(dir, name)) return;
        SaveGlow($"{dir}/{name}.png", size);
    }

    private static void EnsureVignette(string dir, string name, int size)
    {
        if (Exists(dir, name)) return;
        SaveVignette($"{dir}/{name}.png", size);
    }

    // ── 形状 ──

    private static Func<float, float, bool> RoundedRect(int width, int height, float radius)
    {
        float hw = width * 0.5f;
        float hh = height * 0.5f;
        return (u, v) =>
        {
            float px = u * width - hw;
            float py = v * height - hh;
            float qx = Mathf.Max(Mathf.Abs(px) - (hw - radius), 0f);
            float qy = Mathf.Max(Mathf.Abs(py) - (hh - radius), 0f);
            return qx * qx + qy * qy <= radius * radius;
        };
    }

    /// <summary>圆角描边框：外圆角减去内圆角，只留一条随圆角走的细边</summary>
    private static Func<float, float, bool> RoundedRing(int width, int height, float radius, float thickness)
    {
        Func<float, float, bool> outer = RoundedRect(width, height, radius);
        Func<float, float, bool> inner = RoundedRect(
            width - (int)(thickness * 2f), height - (int)(thickness * 2f), Mathf.Max(2f, radius - thickness));
        float ox = (thickness * 2f) / width;
        float oy = (thickness * 2f) / height;
        return (u, v) => outer(u, v) && !inner((u - ox * 0.5f) / (1f - ox), (v - oy * 0.5f) / (1f - oy));
    }

    private static bool Ring(float u, float v)
    {
        float dx = u - 0.5f;
        float dy = v - 0.5f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        return r <= 0.5f && r >= 0.472f;
    }

    /// <summary>圆环外再挂一圈等距小点（参考图头像外的装饰点环）</summary>
    private static bool RingDots(float u, float v)
    {
        float dx = u - 0.5f;
        float dy = v - 0.5f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        if (r >= 0.486f && r <= 0.5f) return true;

        const int dots = 24;
        float angle = Mathf.Atan2(dy, dx);
        float sector = Mathf.PI * 2f / dots;
        float local = angle - Mathf.Round(angle / sector) * sector;
        float arc = Mathf.Abs(local) * 0.42f;
        return Mathf.Abs(r - 0.445f) < 0.012f && arc < 0.02f;
    }

    private static bool TitleMark(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        // 方卡：外白描边 + 内部两条斜线 + 右上小三角，示意"影像"
        if (x >= 6 && x <= 58 && y >= 12 && y <= 52)
        {
            bool border = x < 10 || x > 54 || y < 16 || y > 48;
            float d1 = SegmentDistance(x, y, 14f, 22f, 34f, 44f);
            float d2 = SegmentDistance(x, y, 26f, 18f, 50f, 42f);
            if (border || d1 < 1.6f || d2 < 1.6f) return true;
        }
        return false;
    }

    private static bool Crosshair(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        float dx = x - 32f, dy = y - 32f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        if (r > 30f) return false;
        if (r >= 27f) return true;
        // 十字：中间留空
        bool horizontal = Mathf.Abs(dy) < 1.8f && Mathf.Abs(dx) > 8f && Mathf.Abs(dx) < 26f;
        bool vertical = Mathf.Abs(dx) < 1.8f && Mathf.Abs(dy) > 8f && Mathf.Abs(dy) < 26f;
        return horizontal || vertical;
    }

    private static bool Dots(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        for (int i = 0; i < 3; i++)
        {
            float cy = 46f - i * 14f;
            float dx = x - 32f, dy = y - cy;
            if (dx * dx + dy * dy <= 9f) return true;
        }
        return false;
    }

    private static bool Pen(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        // 笔身：从 (16,48) 到 (48,16) 的粗线 + 笔尖三角
        float d = SegmentDistance(x, y, 18f, 46f, 48f, 16f);
        if (d < 5f) return true;
        if (x < 20f && y > 40f) return (x - 12f) * (x - 12f) + (y - 52f) * (y - 52f) <= 36f;
        return false;
    }

    private static bool Copy(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        bool back = x >= 8 && x <= 40 && y >= 26 && y <= 58;
        bool backBorder = back && (x < 13 || x > 35 || y < 31 || y > 53);
        bool front = x >= 22 && x <= 56 && y >= 8 && y <= 44;
        bool frontBorder = front && (x < 27 || x > 51 || y < 13 || y > 39);
        return backBorder || frontBorder;
    }

    private static bool Info(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        float dx = x - 32f, dy = y - 32f;
        float r = Mathf.Sqrt(dx * dx + dy * dy);
        if (r > 30f) return false;
        if (r >= 27f) return true;
        if (dy > 2f && dy < 8f && Mathf.Abs(dx) < 2.2f) return true;      // 点
        if (dy > -22f && dy < -4f && Mathf.Abs(dx) < 2.2f) return true;   // 竖
        return false;
    }

    /// <summary>眼睛（"隐藏界面"按钮）：上下两条弧 + 中间瞳点</summary>
    private static bool Eye(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        float eyeY = SegmentY(x, 8f, 56f, 32f, 10f);
        float upper = Mathf.Abs(y - eyeY);
        float lowerY = SegmentY(x, 8f, 56f, 4f, 5f);
        bool lid = (upper < 1.8f && x > 9f && x < 55f) || (Mathf.Abs(y - lowerY - 24f) < 1.8f && x > 12f && x < 52f);
        float dx = x - 32f, dy = y - 32f;
        bool pupil = dx * dx + dy * dy <= 25f;
        return lid || pupil;
    }

    /// <summary>抛物线近似：x 从 a 到 b，中心高度 centerY，两端下落到 centerY - drop</summary>
    private static float SegmentY(float x, float a, float b, float centerY, float drop)
    {
        float t = Mathf.Clamp01((x - a) / (b - a));
        float k = (t - 0.5f) * 2f;
        return centerY - drop * (k * k);
    }

    private static bool Play(float u, float v)
    {
        float x = u * 64f, y = v * 64f;
        float dx = x - 32f, dy = y - 32f;
        if (dx * dx + dy * dy > 900f) return false;
        float px = x - 26f;
        float half = (46f - x) * 0.62f;
        return px > 0 && Mathf.Abs(dy) < half;
    }

    // ── 写盘 ──

    private static void SaveMask(string path, int width, int height, Func<float, float, bool> inside, Vector4 border, int maxSize)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        var pixels = new Color32[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int hits = 0;
                for (int sy = 0; sy < 4; sy++)
                {
                    for (int sx = 0; sx < 4; sx++)
                    {
                        float su = (x + (sx + 0.5f) / 4f) / width;
                        float sv = (y + (sy + 0.5f) / 4f) / height;
                        if (inside(su, sv)) hits++;
                    }
                }
                pixels[y * width + x] = new Color32(255, 255, 255, (byte)(hits * 255 / 16));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(path, tex);
        ImportSprite(path, border, maxSize);
    }

    private static void SaveGlow(string path, int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f;
                float dy = (y + 0.5f) / size - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float a = Mathf.Clamp01(1f - r);
                a = a * a * (3f - 2f * a);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(path, tex);
        ImportSprite(path, Vector4.zero, size);
    }

    /// <summary>反向柔光：中心透明、四周变实——压在视频上做暗角，让亮画面不抢文字</summary>
    private static void SaveVignette(string path, int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size - 0.5f;
                float dy = (y + 0.5f) / size - 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy) * 2f;
                float a = Mathf.Clamp01((r - 0.35f) / 0.65f);
                a = a * a * (3f - 2f * a);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }
        tex.SetPixels32(pixels);
        tex.Apply();
        WritePng(path, tex);
        ImportSprite(path, Vector4.zero, size);
    }

    private static void WritePng(string path, Texture2D tex)
    {
        File.WriteAllBytes(path, tex.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    private static void ImportSprite(string path, Vector4 border, int maxSize)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.maxTextureSize = maxSize;

        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteBorder = border;
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();
    }

    public static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }

    public static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
    {
        float vx = bx - ax;
        float vy = by - ay;
        float wx = px - ax;
        float wy = py - ay;
        float lengthSquared = vx * vx + vy * vy;
        float t = lengthSquared > 0f ? Mathf.Clamp01((wx * vx + wy * vy) / lengthSquared) : 0f;
        float dx = wx - vx * t;
        float dy = wy - vy * t;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }
}
#endif
