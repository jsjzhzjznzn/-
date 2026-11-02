using System.IO;

using UnityEditor;

using UnityEngine;

/// <summary>
/// 阿福面板 UI 贴图烘焙（深色科技 HUD 风）。
/// 全部画成白色——消费方用 Image.color 染色，同一张贴图可以既当面板底又当描边框；
/// 圆角矩形/描边框按九宫格导入（border 随 PNG 一起写进 TextureImporter），
/// 所以面板底、气泡、输入框、按钮全部是同一张圆角贴图拉伸出来的，不会糊边。
///
/// 幂等：重复执行覆盖 PNG 并重设导入设置，可在面板重建工具之前随时重跑。
/// 菜单：Tools/阿福面板/生成 UI 贴图
/// </summary>
public static class AgentUiSpriteBaker
{
    private const string OutDir = "Assets/Game/Resources/Agent/ui";

    [MenuItem("Tools/阿福面板/生成 UI 贴图")]
    public static void Bake()
    {
        Directory.CreateDirectory(OutDir);

        // 圆角矩形（填充）：lg=面板卡片底座、md=聊天区/气泡、sm=输入框/按钮/关闭键
        BakeTexture("afu_round_lg", PaintRounded(80, 20f), 24f);
        BakeTexture("afu_round_md", PaintRounded(64, 14f), 18f);
        BakeTexture("afu_round_sm", PaintRounded(48, 10f), 14f);

        // 描边框（中空，九宫格拉伸只保留边线）：与上面同半径，叠在同尺寸矩形上就是 1px 描边
        BakeTexture("afu_frame_lg", PaintFrame(80, 20f, 1.5f), 24f);
        BakeTexture("afu_frame_md", PaintFrame(64, 14f, 1f), 18f);
        BakeTexture("afu_frame_sm", PaintFrame(48, 10f, 1f), 14f);

        // 圆形/圆环/光晕/气泡尾巴
        BakeTexture("afu_circle", PaintCircle(64), 0f);
        BakeTexture("afu_ring", PaintRing(64, 3f), 0f);
        BakeTexture("afu_glow", PaintGlow(96), 0f);
        BakeTexture("afu_tail_left", PaintTail(24, true), 0f);
        BakeTexture("afu_tail_right", PaintTail(24, false), 0f);

        AssetDatabase.Refresh();
        Debug.Log($"[阿福贴图] 完成：11 张贴图已写入 {OutDir}");
    }

    // ──────────────────────────────
    // 写盘与导入设置
    // ──────────────────────────────

    /// <summary>写 PNG 并把导入设置固定成"UI 用 Sprite"（border>0 时按九宫格切）。</summary>
    private static void BakeTexture(string name, Texture2D texture, float border)
    {
        string path = $"{OutDir}/{name}.png";
        File.WriteAllBytes(path, texture.EncodeToPNG());
        Object.DestroyImmediate(texture);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null)
        {
            Debug.LogError($"[阿福贴图] 导入器缺失：{path}");
            return;
        }
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.spritePixelsPerUnit = 100f;
        // 九宫格边界（左/下/右/上）；半径 10 的小圆角给 14 是留 1.4 倍余量防拉伸糊角
        importer.spriteBorder = border > 0f ? new Vector4(border, border, border, border) : Vector4.zero;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 256;
        // 白图 + 运行时染色：压缩块效应会显在圆角边缘上，几十 KB 的贴图不值得省这点体积
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
    }

    // ──────────────────────────────
    // 程序化绘制（SDF 距离场 + 1px 抗锯齿）
    // ──────────────────────────────

    private static Texture2D NewTexture(int size)
    {
        return new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
    }

    /// <summary>圆角矩形填充（到圆角矩形的有符号距离 → 1px 软边）。</summary>
    private static Texture2D PaintRounded(int size, float radius)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        float extent = half - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = RoundedDistance(new Vector2(x - half, y - half), extent, radius);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        }
        return texture;
    }

    /// <summary>圆角描边框（中空）：只有 |d| &lt; thickness/2 的一圈有像素。</summary>
    private static Texture2D PaintFrame(int size, float radius, float thickness)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        float extent = half - 0.5f - thickness * 0.5f;   // 内缩半个线宽，让线不糊到贴图外沿
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Abs(RoundedDistance(new Vector2(x - half, y - half), extent, radius)) - thickness * 0.5f;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        }
        return texture;
    }

    /// <summary>圆角矩形有符号距离（内部为负）。</summary>
    private static float RoundedDistance(Vector2 p, float extent, float radius)
    {
        Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - new Vector2(extent - radius, extent - radius);
        Vector2 clamped = new Vector2(Mathf.Max(q.x, 0f), Mathf.Max(q.y, 0f));
        return clamped.magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - radius;
    }

    /// <summary>实心圆（1.5px 软边；头像圆遮罩/在线点/思考点共用）。</summary>
    private static Texture2D PaintCircle(int size)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        float radius = half - 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(half, half)) - radius;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        }
        return texture;
    }

    /// <summary>圆环（头像外圈用）。</summary>
    private static Texture2D PaintRing(int size, float thickness)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        float radius = half - 0.5f - thickness * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Abs(Vector2.Distance(new Vector2(x, y), new Vector2(half, half)) - radius) - thickness * 0.5f;
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d)));
            }
        }
        return texture;
    }

    /// <summary>径向光晕（头像背后的暖光；二次方衰减，中心 1 边缘 0）。</summary>
    private static Texture2D PaintGlow(int size)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float t = Vector2.Distance(new Vector2(x, y), new Vector2(half, half)) / half;
                float a = Mathf.Pow(Mathf.Clamp01(1f - t), 2.2f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        return texture;
    }

    /// <summary>气泡尾巴：等腰三角形，底边贴贴图的一侧（贴在气泡边上不露缝）。</summary>
    private static Texture2D PaintTail(int size, bool pointLeft)
    {
        Texture2D texture = NewTexture(size);
        float half = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = pointLeft ? x / (float)(size - 1) : 1f - x / (float)(size - 1);   // 0=尖、1=底边
                float halfExtent = u * (half - 0.5f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(halfExtent - Mathf.Abs(y - half) + 0.5f)));
            }
        }
        return texture;
    }
}
