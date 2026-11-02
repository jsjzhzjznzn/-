#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;

/// <summary>
/// 大世界小地图底图烘焙器（离线，一次性产物）。
/// 菜单：Tools/地图/烘焙大世界小地图
/// 产物：Assets/Game/Resources/Minimap/dashijie_minimap.png（1536×1536）
///
/// 流程：1m 步长向下打射线 → 取地面高度/材质/遮挡高度 → 从出生点洪水填充算可达 →
/// 采样材质 _BaseMap 平均色 → 按遮挡高度压暗 → 描边 → 每格填 4×4 像素输出。
/// 常量来自 <see cref="DashijieMinimap"/>，与运行时共用，改一处必须重烤。
/// </summary>
public static class DashijieMinimapBaker
{
    private const string ScenePath = "Assets/Game/Scenes/dashijie.scene";
    private const string OutputPath = "Assets/Game/Resources/Minimap/dashijie_minimap.png";
    private const int GroundLayer = 6;
    private const float RayStartY = 60f;
    private const float RayLength = 200f;

    /// <summary>出生点（洪水填充种子）</summary>
    private static readonly Vector2 SpawnXZ = new(0f, -6f);

    /// <summary>可达连通的高度差容差</summary>
    private const float WalkableStep = 1.2f;

    /// <summary>遮挡高度分档压暗（0m / 3m / 6m / 12m / 25m 以上）</summary>
    private static readonly float[] ShadeHeights = { 0f, 3f, 6f, 12f, 25f };
    private static readonly float[] ShadeFactors = { 1f, 0.9f, 0.8f, 0.68f, 0.55f };

    // 无地面 / 不可达都画成「石板灰」而不是纯黑：大世界处处是建筑夹缝，纯黑容易被误读成地图黑边
    private static readonly Color32 NoGroundColor = new(34, 38, 46, 255);
    private static readonly Color32 UnreachableColor = new(44, 50, 60, 255);
    private static readonly Color32 OutlineColor = new(20, 23, 28, 255);
    private static readonly Color32 FallbackColor = new(150, 150, 150, 255);

    [MenuItem("Tools/地图/烘焙大世界小地图")]
    public static void Bake()
    {
        if (EditorApplication.isPlaying)
        {
            Debug.LogError("[MinimapBaker] 必须在非播放态烘焙（播放态场景位置会被改写）");
            return;
        }

        if (EditorSceneManager.GetActiveScene().path != ScenePath)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        int cells = Mathf.RoundToInt(DashijieMinimap.WorldSize);
        float half = DashijieMinimap.WorldSize * 0.5f;

        bool[] hasGround = new bool[cells * cells];
        float[] floorY = new float[cells * cells];
        Material[] floorMaterial = new Material[cells * cells];
        float[] topY = new float[cells * cells];
        var materialLookup = new Dictionary<string, Material>();

        // ── 1) 打射线 ──
        for (int zi = 0; zi < cells; zi++)
        {
            float z = -half + zi + 0.5f;
            for (int xi = 0; xi < cells; xi++)
            {
                float x = -half + xi + 0.5f;
                int index = zi * cells + xi;

                RaycastHit[] hits = Physics.RaycastAll(
                    new Vector3(x, RayStartY, z), Vector3.down, RayLength, 1 << GroundLayer, QueryTriggerInteraction.Ignore);

                if (hits.Length == 0) continue;

                topY[index] = hits.Max(h => h.point.y);

                RaycastHit? floor = null;
                foreach (RaycastHit hit in hits.OrderBy(h => h.distance))
                {
                    if (hit.point.y < DashijieMinimap.FloorMinY || hit.point.y > DashijieMinimap.FloorMaxY) continue;
                    if (hit.normal.y <= DashijieMinimap.FloorNormalY) continue;
                    floor = hit;
                    break;
                }

                if (!floor.HasValue) continue;

                hasGround[index] = true;
                floorY[index] = floor.Value.point.y;

                Material shared = floor.Value.collider.GetComponent<Renderer>()?.sharedMaterial;
                if (shared != null)
                {
                    floorMaterial[index] = shared;
                    materialLookup[shared.name] = shared;
                }
            }
        }

        // ── 2) 可达性洪水填充（4 邻域，高度差 ≤ WalkableStep） ──
        bool[] reachable = new bool[cells * cells];
        int seedX = Mathf.Clamp(Mathf.FloorToInt(SpawnXZ.x + half), 0, cells - 1);
        int seedZ = Mathf.Clamp(Mathf.FloorToInt(SpawnXZ.y + half), 0, cells - 1);
        int reachableCount = 0;

        var queue = new Queue<int>(8192);
        int seedIndex = seedZ * cells + seedX;
        if (hasGround[seedIndex])
        {
            reachable[seedIndex] = true;
            queue.Enqueue(seedIndex);
        }
        else
        {
            Debug.LogWarning("[MinimapBaker] 出生点没有地面命中，可达区从第一个地面格起算");
            for (int i = 0; i < hasGround.Length; i++)
            {
                if (!hasGround[i]) continue;
                reachable[i] = true;
                queue.Enqueue(i);
                break;
            }
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            reachableCount++;
            int xi = index % cells;
            int zi = index / cells;
            float baseY = floorY[index];

            TryEnqueue(xi - 1, zi, baseY);
            TryEnqueue(xi + 1, zi, baseY);
            TryEnqueue(xi, zi - 1, baseY);
            TryEnqueue(xi, zi + 1, baseY);
        }

        void TryEnqueue(int nx, int nz, float baseY)
        {
            if (nx < 0 || nx >= cells || nz < 0 || nz >= cells) return;
            int nIndex = nz * cells + nx;
            if (reachable[nIndex] || !hasGround[nIndex]) return;
            if (Mathf.Abs(floorY[nIndex] - baseY) > WalkableStep) return;
            reachable[nIndex] = true;
            queue.Enqueue(nIndex);
        }

        // ── 3) 材质平均色（_BaseColor 全白，必须采样 _BaseMap） ──
        var colorCache = new Dictionary<string, Color32>();

        Color32 CellColor(int index)
        {
            if (!hasGround[index]) return NoGroundColor;
            if (!reachable[index]) return UnreachableColor;

            Material material = floorMaterial[index];
            string key = material != null ? material.name : "none";
            if (!colorCache.TryGetValue(key, out Color32 baseColor))
            {
                baseColor = material != null ? AverageTextureColor(material) : FallbackColor;
                colorCache[key] = baseColor;
            }

            float cover = Mathf.Max(0f, topY[index] - floorY[index]);
            float shade = 1f;
            for (int i = 0; i < ShadeHeights.Length; i++)
            {
                if (cover >= ShadeHeights[i]) shade = ShadeFactors[i];
            }

            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(baseColor.r * shade), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(baseColor.g * shade), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(baseColor.b * shade), 0, 255),
                255);
        }

        // ── 4) 上采样到 1536²（每格 4×4，最近邻）+ 描边 ──
        int texSize = DashijieMinimap.TextureSize;
        int ppc = DashijieMinimap.PixelsPerMeter;
        var pixels = new Color32[texSize * texSize];

        for (int zi = 0; zi < cells; zi++)
        {
            for (int xi = 0; xi < cells; xi++)
            {
                int index = zi * cells + xi;
                Color32 color = hasGround[index] && IsEdge(xi, zi) ? OutlineColor : CellColor(index);

                int px0 = xi * ppc;
                int py0 = zi * ppc;
                for (int dy = 0; dy < ppc; dy++)
                {
                    int row = (py0 + dy) * texSize + px0;
                    for (int dx = 0; dx < ppc; dx++) pixels[row + dx] = color;
                }
            }
        }

        bool IsEdge(int xi, int zi)
        {
            if (xi == 0 || zi == 0 || xi == cells - 1 || zi == cells - 1) return true;
            int index = zi * cells + xi;
            if (!hasGround[index]) return false;
            return !hasGround[index - 1] || !hasGround[index + 1]
                   || !hasGround[index - cells] || !hasGround[index + cells];
        }

        // ── 5) 输出 PNG（第 0 行 = 世界 Z 最小侧，与 UV 自下而上一致） ──
        var texture = new Texture2D(texSize, texSize, TextureFormat.RGB24, false);
        texture.SetPixels32(pixels);
        texture.Apply(false, false);

        EnsureFolder(Path.GetDirectoryName(OutputPath)?.Replace("\\", "/"));
        File.WriteAllBytes(OutputPath, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture);
        AssetDatabase.ImportAsset(OutputPath, ImportAssetOptions.ForceUpdate);

        var importer = (TextureImporter)AssetImporter.GetAtPath(OutputPath);
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.maxTextureSize = 2048;
            // 1536 不是 2 的幂：默认 ToNearest 会放大到 2048（白占内存），契约按 1536 算，必须保持原尺寸
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        // 可达区 bbox（世界坐标）
        int minXi = cells, maxXi = -1, minZi = cells, maxZi = -1;
        for (int zi = 0; zi < cells; zi++)
        for (int xi = 0; xi < cells; xi++)
        {
            if (!reachable[zi * cells + xi]) continue;
            if (xi < minXi) minXi = xi;
            if (xi > maxXi) maxXi = xi;
            if (zi < minZi) minZi = zi;
            if (zi > maxZi) maxZi = zi;
        }

        AssetDatabase.Refresh();
        sw.Stop();
        Debug.Log($"[MinimapBaker] 完成 {OutputPath}\n" +
                  $"  地面格={hasGround.Count(g => g)} 可达格={reachableCount} 材质={colorCache.Count}\n" +
                  $"  可达 bbox: X[{minXi - half:F0},{maxXi + 1 - half:F0}] Z[{minZi - half:F0},{maxZi + 1 - half:F0}]\n" +
                  $"  尺寸={texSize}² 耗时={sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 采样材质 _BaseMap 的平均色。
    /// FBX 内嵌贴图通常不可读（isReadable=false），所以先试 GetPixels32，失败再 Blit 到小 RT 读回。
    /// </summary>
    private static Color32 AverageTextureColor(Material material)
    {
        if (!material.HasProperty("_BaseMap")) return FallbackColor;
        var texture = material.GetTexture("_BaseMap");
        if (texture == null) return FallbackColor;

        if (texture is Texture2D readable && readable.isReadable)
        {
            Color32[] pixels = readable.GetPixels32();
            if (pixels.Length == 0) return FallbackColor;

            int stride = Mathf.Max(1, pixels.Length / 4096);
            long r = 0, g = 0, b = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += stride)
            {
                r += pixels[i].r; g += pixels[i].g; b += pixels[i].b;
                count++;
            }

            return new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
        }

        return AverageByBlit(texture);
    }

    private static Color32 AverageByBlit(Texture texture)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        try
        {
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var sample = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
            sample.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
            sample.Apply(false, false);

            Color[] pixels = sample.GetPixels();
            UnityEngine.Object.DestroyImmediate(sample);

            float r = 0f, g = 0f, b = 0f;
            foreach (Color pixel in pixels) { r += pixel.r; g += pixel.g; b += pixel.b; }
            r /= pixels.Length; g /= pixels.Length; b /= pixels.Length;

            // Blit 走线性空间，写回 sRGB 贴图前转回 gamma
            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                r = Mathf.LinearToGammaSpace(r);
                g = Mathf.LinearToGammaSpace(g);
                b = Mathf.LinearToGammaSpace(b);
            }

            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g * 255f), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b * 255f), 0, 255),
                255);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
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
