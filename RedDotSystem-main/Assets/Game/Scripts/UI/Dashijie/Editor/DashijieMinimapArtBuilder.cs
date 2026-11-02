#if UNITY_EDITOR
using System;
using System.IO;

using UnityEditor;

using UnityEngine;

/// <summary>
/// 小地图/大地图用到的美术与材质（可重复执行）。
/// 菜单：Tools/UI/生成小地图美术
///
/// 产物：
///   Assets/Game/UI/Dashijie/Icons/dashijie_icon_map.png  热键行的「地图」图标（52×52 风格，256² 白描边）
///   Assets/Game/UI/Dashijie/Icons/minimap_arrow.png      玩家朝向箭头
///   Assets/Game/UI/Dashijie/Icons/minimap_ring.png       小地图金色外环
///
/// 全部程序化绘制：不额外引入贴图资产，颜色/尺寸改这里即可。
/// </summary>
public static class DashijieMinimapArtBuilder
{
    private const string IconFolder = "Assets/Game/UI/Dashijie/Icons";

    private static readonly Color White = new(1f, 1f, 1f, 1f);
    private static readonly Color Gold = new(0.910f, 0.769f, 0.416f, 1f);

    [MenuItem("Tools/UI/生成小地图美术")]
    public static void Build()
    {
        EnsureFolder(IconFolder);

        WritePng($"{IconFolder}/dashijie_icon_map.png", 256, DrawMapGlyph);
        WritePng($"{IconFolder}/minimap_arrow.png", 128, DrawArrow);
        WritePng($"{IconFolder}/minimap_ring.png", 512, DrawRing);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[MinimapArt] 图标已生成");
    }

    // ──────────────────────────────
    // 图形
    // ──────────────────────────────

    /// <summary>折页地图：圆角矩形描边 + 两条折线 + 中心定位点</summary>
    private static Color32 DrawMapGlyph(float x, float y, int size)
    {
        Vector2 p = new(x - size * 0.5f, y - size * 0.5f);
        float half = size * 0.34f;
        float radius = size * 0.07f;
        float stroke = size * 0.045f;

        float dBox = Mathf.Abs(SdRoundBox(p, new Vector2(half, half * 0.86f), radius)) - stroke * 0.5f;

        // 两条折线（略微梯形，避免和边框线重影）
        float fold = Mathf.Min(
            Mathf.Abs(p.x + half * 0.34f) - stroke * 0.45f,
            Mathf.Abs(p.x - half * 0.34f) - stroke * 0.45f);
        fold = fold <= 0f && Mathf.Abs(p.y) < half * 0.86f - stroke ? fold : float.MaxValue;

        float dot = Vector2.Distance(p, new Vector2(0f, -half * 0.16f)) - size * 0.055f;

        float d = Mathf.Min(dBox, Mathf.Min(fold, dot));
        return AlphaFromDistance(d, size);
    }

    /// <summary>向上的箭头（尖端在上，尾部有柄）</summary>
    private static Color32 DrawArrow(float x, float y, int size)
    {
        Vector2 p = new(x - size * 0.5f, y - size * 0.5f);
        float s = size * 0.5f;

        Vector2 tip = new(0f, s * 0.72f);
        Vector2 left = new(-s * 0.62f, s * 0.02f);
        Vector2 right = new(s * 0.62f, s * 0.02f);
        Vector2 notchLeft = new(-s * 0.28f, s * 0.02f);
        Vector2 notchRight = new(s * 0.28f, s * 0.02f);

        // 三角箭头 + 箭柄
        float dTri = Mathf.Min(
            Mathf.Min(DistanceToSegment(p, tip, left), DistanceToSegment(p, left, notchLeft)),
            Mathf.Min(DistanceToSegment(p, notchLeft, notchRight), DistanceToSegment(p, notchRight, right)),
            Mathf.Min(DistanceToSegment(p, right, tip), Mathf.Min(
                DistanceToSegment(p, notchLeft, new Vector2(-s * 0.2f, -s * 0.62f)),
                DistanceToSegment(p, notchRight, new Vector2(s * 0.2f, -s * 0.62f)))));

        bool inside = InsidePolygon(p, new[] { tip, left, notchLeft, new Vector2(-s * 0.2f, -s * 0.62f), new Vector2(s * 0.2f, -s * 0.62f), notchRight, right });

        float d = inside ? -size * 0.5f : dTri;
        return AlphaFromDistance(d, size);
    }

    /// <summary>金色圆环（中心透明，可盖在圆形地图上）</summary>
    private static Color32 DrawRing(float x, float y, int size)
    {
        Vector2 p = new(x - size * 0.5f, y - size * 0.5f);
        float radius = size * 0.5f - size * 0.02f;
        float width = size * 0.016f;
        float d = Mathf.Abs(p.magnitude - radius) - width * 0.5f;
        return AlphaFromDistance(d, size, Gold);
    }

    // ──────────────────────────────
    // 绘制工具
    // ──────────────────────────────

    private static Color32 AlphaFromDistance(float distance, int size, Color? color = null)
    {
        Color c = color ?? White;
        // 1.5px 抗锯齿
        float alpha = Mathf.Clamp01(0.5f - distance / 1.5f);
        return new Color32(
            (byte)Mathf.RoundToInt(c.r * 255f),
            (byte)Mathf.RoundToInt(c.g * 255f),
            (byte)Mathf.RoundToInt(c.b * 255f),
            (byte)Mathf.RoundToInt(alpha * 255f));
    }

    private static float SdRoundBox(Vector2 p, Vector2 half, float radius)
    {
        Vector2 q = new(Mathf.Abs(p.x) - half.x + radius, Mathf.Abs(p.y) - half.y + radius);
        Vector2 outside = new(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f));
        return outside.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
        return Vector2.Distance(p, a + ab * t);
    }

    private static bool InsidePolygon(Vector2 p, Vector2[] polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if (polygon[i].y > p.y != polygon[j].y > p.y &&
                p.x < (polygon[j].x - polygon[i].x) * (p.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    private static void WritePng(string path, int size, Func<float, float, int, Color32> draw)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
            pixels[y * size + x] = draw(x + 0.5f, y + 0.5f, size);

        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null) return;
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    private static void EnsureClipMaterial()
    {
        // 圆形裁剪已改用 uGUI Mask（见 DashijieHudViewBuilder），不再需要自定义材质
    }

    private static void EnsureFolder(string path)
    {
        if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path)?.Replace("\\", "/");
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
