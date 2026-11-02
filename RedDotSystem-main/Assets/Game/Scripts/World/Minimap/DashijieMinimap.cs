using UnityEngine;

/// <summary>
/// 大世界小地图的世界契约（烘焙工具与运行时共用的唯一真源）。
///
/// 四件套必须同步：覆盖范围/中心、朝向、PixelsPerMeter、底图路径。
/// 改任何一项都要重新烘焙底图，否则地图会错位/缩放错误。
/// </summary>
public sealed class DashijieMinimap
{
    /// <summary>底图中心对应的世界 X（世界 XZ 的 X）</summary>
    public const float WorldCenterX = 0f;

    /// <summary>底图中心对应的世界 Z</summary>
    public const float WorldCenterZ = 0f;

    /// <summary>底图覆盖的世界边长（米）：X∈[-192,192]、Z∈[-192,192]</summary>
    public const float WorldSize = 384f;

    /// <summary>贴图固有比例：每米多少像素（烘焙常量，运行时不改；缩放只改 UI）</summary>
    public const int PixelsPerMeter = 4;

    /// <summary>底图边长（像素）= WorldSize × PixelsPerMeter</summary>
    public const int TextureSize = 1536;

    /// <summary>Resources 下的底图路径（不带扩展名）</summary>
    public const string ResourcePath = "Minimap/dashijie_minimap";

    /// <summary>地面层高度窗口：只在这个区间内的命中算地面</summary>
    public const float FloorMinY = -3.5f;
    public const float FloorMaxY = 6f;

    /// <summary>地面法线 y 分量下限（排除墙面与天花板）</summary>
    public const float FloorNormalY = 0.6f;

    /// <summary>已加载的底图（Unload 前常驻）</summary>
    public Texture2D Texture { get; private set; }

    /// <summary>加载烘焙底图；失败返回 false 且 Texture 为 null</summary>
    public bool Load()
    {
        if (Texture != null) return true;
        Texture = Resources.Load<Texture2D>(ResourcePath);
        return Texture != null;
    }

    /// <summary>释放底图引用（资源本体由 Resources.UnloadAsset 处理）</summary>
    public void Destroy()
    {
        if (Texture == null) return;
        Resources.UnloadAsset(Texture);
        Texture = null;
    }

    /// <summary>
    /// 世界 XZ → 底图像素坐标（左下原点，+X 向右、+Z 向上）。
    /// </summary>
    public static Vector2 WorldToMap(Vector2 worldXZ)
    {
        float px = (worldXZ.x - WorldCenterX) * PixelsPerMeter + TextureSize * 0.5f;
        float py = (worldXZ.y - WorldCenterZ) * PixelsPerMeter + TextureSize * 0.5f;
        return new Vector2(px, py);
    }

    /// <summary>底图像素坐标（左下原点）→ 世界 XZ</summary>
    public static Vector2 MapToWorld(Vector2 mapPx)
    {
        float x = (mapPx.x - TextureSize * 0.5f) / PixelsPerMeter + WorldCenterX;
        float z = (mapPx.y - TextureSize * 0.5f) / PixelsPerMeter + WorldCenterZ;
        return new Vector2(x, z);
    }
}
