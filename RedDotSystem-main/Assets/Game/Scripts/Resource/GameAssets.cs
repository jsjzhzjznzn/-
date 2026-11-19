using System.Collections.Generic;
using SkierFramework;
using UnityEngine;
using YooAsset;

/// <summary>
/// UI 资源统一门面：同一个 location 先查 Resources（尚未迁移的内置资源），再查 YooAsset（已移出 Resources 的热更资源）。
///
/// location 约定为完整资源路径，与 UIConfig / ResourceManager 一致：
///   "Assets/Game/Art/UI/Theme/panel_bg.png"
///
/// 为什么两个分支都要：迁移是分批进行的，搬迁中的资源必须能立刻找到；
/// Resources 命中即返回、不产生句柄；YooAsset 分支的句柄按 location 常驻缓存，
/// 因为这些是跨界面共用的 UI 图，生命周期与包一致，不参与 ResourceManager 的引用计数。
///
/// 调用约定：必须在 YooAsset 包初始化后使用（GUI 界面都在那之后打开）；
/// 更早的加载点（字体回退、Lua、框架资产）继续直接 Resources.Load。
/// </summary>
public static class GameAssets
{
    /// <summary>Resources 相对路径前缀（Resources.Load 用的是去掉这一段之后的部分）</summary>
    private const string ResourcesRoot = "Assets/Game/Resources/";

    /// <summary>location → 已加载资源（Resources 与 YooAsset 分支共用）</summary>
    private static readonly Dictionary<string, Object> cache = new();

    /// <summary>location → YooAsset 句柄（常驻，界面关闭时不释放）</summary>
    private static readonly Dictionary<string, AssetHandle> handles = new();

    /// <summary>已警告过的 location，避免每帧刷屏</summary>
    private static readonly HashSet<string> missing = new();

    /// <summary>取 Sprite（同步；找不到返回 null 并只警告一次）</summary>
    public static Sprite GetSprite(string location)
    {
        return GetAsset<Sprite>(location);
    }

    /// <summary>取任意资源（同步；找不到返回 null）</summary>
    public static T GetAsset<T>(string location) where T : Object
    {
        return GetAssetInternal<T>(location, warn: true);
    }

    /// <summary>
    /// 取任意资源（同步）。warn=false 时不打"未找到"警告，也不记入失败集合，
    /// 供"按多个候选目录依次尝试"的调用方使用（真正全都没命中时由调用方自己警告一次）。
    /// </summary>
    private static T GetAssetInternal<T>(string location, bool warn) where T : Object
    {
        if (string.IsNullOrEmpty(location)) return null;
        if (cache.TryGetValue(location, out Object cached))
        {
            // Unity 假 null（资源被卸载）时清掉缓存重新加载
            if (cached != null) return cached as T;
            cache.Remove(location);
        }
        if (warn && missing.Contains(location)) return null;

        T asset = LoadFromResources<T>(location);
        if (asset == null) asset = LoadFromPackage<T>(location);

        if (asset == null)
        {
            if (warn)
            {
                missing.Add(location);
                GameLog.Warn($"[GameAssets] 资源未找到：{location}（Resources 与 YooAsset 均未命中）");
            }
            return null;
        }

        cache[location] = asset;
        return asset;
    }

    /// <summary>尝试取资源（不打印警告，用于"可有可无"或"多目录顺序查找"的场景）</summary>
    public static bool TryGetAsset<T>(string location, out T asset) where T : Object
    {
        asset = GetAssetInternal<T>(location, warn: false);
        return asset != null;
    }

    /// <summary>
    /// 按目录取全部 Sprite（表情包这类约定式目录用）。
    /// Resources 目录存在时走 LoadAll；否则逐个按 <paramref name="locations"/> 加载 YooAsset 资源。
    /// </summary>
    public static Sprite[] LoadAllSprites(string resourcesFolder, IReadOnlyList<string> locations = null)
    {
        if (!string.IsNullOrEmpty(resourcesFolder))
        {
            Sprite[] all = Resources.LoadAll<Sprite>(resourcesFolder);
            if (all != null && all.Length > 0) return all;
        }

        if (locations == null || locations.Count == 0) return System.Array.Empty<Sprite>();

        var result = new List<Sprite>(locations.Count);
        for (int i = 0; i < locations.Count; i++)
        {
            Sprite sprite = GetSprite(locations[i]);
            if (sprite != null) result.Add(sprite);
        }
        return result.ToArray();
    }

    /// <summary>YooAsset 是否已就绪（早期调用可据此决定是否回退 Resources）</summary>
    public static bool IsPackageReady =>
        YooAssetService.Instance != null && YooAssetService.Instance.IsInitialized;

    private static T LoadFromResources<T>(string location) where T : Object
    {
        if (!location.StartsWith(ResourcesRoot, System.StringComparison.Ordinal)) return null;
        string relative = location.Substring(ResourcesRoot.Length);
        return Resources.Load<T>(relative);
    }

    private static T LoadFromPackage<T>(string location) where T : Object
    {
        if (!IsPackageReady) return null;
        if (!YooAssetService.Instance.IsLocationValid(location)) return null;

        AssetHandle handle = YooAssetService.Instance.LoadAssetSync<T>(location);
        if (handle == null || handle.Status != EOperationStatus.Succeeded)
        {
            handle?.Release();
            return null;
        }

        handles[location] = handle;
        return handle.GetAssetObject<T>();
    }

    /// <summary>清空缓存与句柄（换账号/热重载调试用；正常不调用）</summary>
    public static void Clear()
    {
        foreach (AssetHandle handle in handles.Values) handle?.Release();
        handles.Clear();
        cache.Clear();
        missing.Clear();
    }
}
