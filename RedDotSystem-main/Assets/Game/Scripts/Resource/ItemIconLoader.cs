using System.Collections.Generic;

using UnityEngine;

/// <summary>
/// 物品图标加载器（原生 Resources 版，不依赖任何第三方资源框架）
/// 约定：iconKey = item_config.icon_key = 图标文件名（不含扩展名）；
///       图标文件放在 Assets/Game/Resources/ui/{子目录}/{iconKey}.png，按 SearchFolders 顺序逐个目录查找
/// 特点：同步加载（Resources.Load）+ 命中缓存 + 失败记忆（找不到的键只警告一次，不反复查找刷屏）
/// 注意：同步加载意味着调用方拿到的就是"当前已就绪"的图（未找到返回 null），
///       不需要异步回调、请求合并、令牌防串味那套机制（格子复用直接重新 Get 即可）
/// </summary>
public static class ItemIconLoader
{
    /// <summary>图标搜索目录（相对 Assets/Game/Resources，末尾必须带 /）。新增图标目录时在这里补一项即可。</summary>
    private static readonly string[] SearchFolders = { "ui/80x80/", "ui/鸣潮：武器图标/" };

    /// <summary>iconKey → 已加载好的 Sprite。</summary>
    private static readonly Dictionary<string, Sprite> cache = new();

    /// <summary>找不到的 iconKey（记住，避免每次滚动都重复 Resources.Load 与刷屏）。</summary>
    private static readonly HashSet<string> missing = new();

    // ──────────────────────────────
    // 对外接口
    // ──────────────────────────────

    /// <summary>
    /// 取图标的方法（同步）：命中缓存直接返回；未命中则按目录顺序 Resources.Load 一次并缓存结果。
    /// </summary>
    /// <param name="iconKey">参数名：iconKey，类型：string，用途：图标资源键（= 图标文件名，无扩展名）</param>
    /// <returns>返回值类型：Sprite，用途：图标（未找到为 null）</returns>
    public static Sprite Get(string iconKey)
    {
        if (string.IsNullOrEmpty(iconKey)) return null;
        if (cache.TryGetValue(iconKey, out Sprite cached)) return cached;
        if (missing.Contains(iconKey)) return null;

        foreach (string folder in SearchFolders)
        {
            Sprite sprite = Resources.Load<Sprite>(folder + iconKey);
            if (sprite != null)
            {
                cache[iconKey] = sprite;
                return sprite;
            }
        }

        missing.Add(iconKey);
        Debug.LogWarning($"[ItemIconLoader] 图标未找到：{iconKey}（已查找 {string.Join("、", SearchFolders)}；图标需放在 Assets/Game/Resources/ 下且导入类型为 Sprite）");
        return null;
    }

    /// <summary>图标是否已加载过（调试用）。</summary>
    /// <param name="iconKey">参数名：iconKey，类型：string，用途：图标资源键</param>
    /// <returns>返回值类型：bool，用途：缓存中是否已有该图标</returns>
    public static bool IsLoaded(string iconKey) => !string.IsNullOrEmpty(iconKey) && cache.ContainsKey(iconKey);

    /// <summary>清空缓存与失败记忆（换账号/热重载调试时用；正常不需要调用）。</summary>
    public static void Clear()
    {
        cache.Clear();
        missing.Clear();
    }
}
