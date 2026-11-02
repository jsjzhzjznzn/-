using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using UnityEngine;

/// <summary>
/// 单个表情包条目（静态图或逐帧动画）
/// </summary>
public sealed class StickerInfo
{
    /// <summary>表情键（= 图标文件名 / 动画目录名，也就是聊天消息里传输的内容）</summary>
    public string Key = string.Empty;

    /// <summary>是否为逐帧动画（帧数 &gt; 1）</summary>
    public bool Animated => Frames.Length > 1;

    /// <summary>帧序列：静态表情只有一帧</summary>
    public Sprite[] Frames = Array.Empty<Sprite>();

    /// <summary>播放帧率（动画用；从帧目录里的 sticker.txt 读取，读不到用默认值）</summary>
    public float Fps = StickerCatalog.DefaultFps;

    /// <summary>面板预览图（用第一帧）</summary>
    public Sprite Thumbnail => Frames.Length > 0 ? Frames[0] : null;

    /// <summary>宽高比（宽/高），气泡按它等比缩放，避免拉伸变形</summary>
    public float Aspect
    {
        get
        {
            Sprite sprite = Thumbnail;
            if (sprite == null || sprite.rect.height <= 0f) return 1f;
            return sprite.rect.width / sprite.rect.height;
        }
    }
}

/// <summary>
/// 表情包目录（运行时资源目录 Assets/Game/Resources/biaoq）
/// 约定式自动发现，不需要任何清单文件：
///   · Assets/Game/Resources/biaoq/&lt;键&gt;.png|jpg          → 静态表情（文件名就是键）
///   · Assets/Game/Resources/biaoq/&lt;键&gt;/&lt;键&gt;_NNN.png     → 动画表情的帧（由 extract_gif_frames.ps1 生成）
///   · Assets/Game/Resources/biaoq/&lt;键&gt;/sticker.txt        → 动画播放帧率（可选）
/// 因此「往 Resources/biaoq 丢图 = 多一个表情」，客户端零改动；GIF 本体留在 Assets/Game/Art/biaoq 不进包。
/// 聊天消息里只传「键」，客户端按键取图——服务端不关心表情长什么样，只透传内容类型。
/// </summary>
public static class StickerCatalog
{
    /// <summary>表情资源目录（Resources 相对路径，不含 Assets/Game/Resources 前缀）</summary>
    public const string ResourceFolder = "biaoq";

    /// <summary>动画帧的默认播放帧率（sticker.txt 缺失时用）</summary>
    public const float DefaultFps = 12f;

    /// <summary>帧文件命名约定：&lt;键&gt;_&lt;三位序号&gt;（如 DAC41..._007）</summary>
    private static readonly Regex FrameNamePattern = new(@"^(?<key>.+)_(?<index>\d{3})$", RegexOptions.Compiled);

    /// <summary>全部表情（面板按这个顺序展示）</summary>
    private static List<StickerInfo> all;

    /// <summary>键→表情（渲染气泡时按消息内容里的键取用）</summary>
    private static Dictionary<string, StickerInfo> byKey;

    /// <summary>全部表情的只读列表（首次访问时加载；面板与 E2E 用）</summary>
    public static IReadOnlyList<StickerInfo> All
    {
        get
        {
            EnsureLoaded();
            return all;
        }
    }

    /// <summary>
    /// 按表情键取条目的方法
    /// </summary>
    /// <param name="key">参数名：key，类型：string，用途：消息内容里的表情键</param>
    /// <param name="info">参数名：info，类型：out StickerInfo，用途：取到的表情条目</param>
    /// <returns>返回值类型：bool，用途：true=该键是本客户端认识的表情</returns>
    public static bool TryGet(string key, out StickerInfo info)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(key))
        {
            info = null;
            return false;
        }
        return byKey.TryGetValue(key, out info);
    }

    /// <summary>
    /// 确保表情目录已加载的方法（幂等；加载失败/目录为空都只算"没有表情"，不抛异常）
    /// </summary>
    public static void EnsureLoaded()
    {
        if (all != null) return;
        Build();
    }

    /// <summary>清掉缓存重新扫描（编辑器里增删表情资源后调试用；运行时不调用）</summary>
    public static void Invalidate()
    {
        all = null;
        byKey = null;
    }

    /// <summary>
    /// 扫描表情目录的方法
    /// 一次 Resources.LoadAll&lt;Sprite&gt; 拿到目录及子目录的全部 Sprite：
    /// 名字符合「键_序号」的归到同一个动画表情的帧序列里，其余各自算一个静态表情
    /// </summary>
    private static void Build()
    {
        all = new List<StickerInfo>();
        byKey = new Dictionary<string, StickerInfo>(StringComparer.Ordinal);

        // 键 → (序号 → 帧)，序号用来给帧排序（文件名三位数补零，字典序即帧序）
        Dictionary<string, SortedDictionary<int, Sprite>> frameGroups = new(StringComparer.Ordinal);

        foreach (Sprite sprite in Resources.LoadAll<Sprite>(ResourceFolder))
        {
            if (sprite == null) continue;

            Match match = FrameNamePattern.Match(sprite.name);
            if (match.Success && int.TryParse(match.Groups["index"].Value, out int index))
            {
                string frameKey = match.Groups["key"].Value;
                if (!frameGroups.TryGetValue(frameKey, out SortedDictionary<int, Sprite> frames))
                {
                    frames = new SortedDictionary<int, Sprite>();
                    frameGroups[frameKey] = frames;
                }
                frames[index] = sprite;
                continue;
            }

            AddSticker(sprite.name, new[] { sprite }, DefaultFps);
        }

        int animatedCount = 0;
        foreach (KeyValuePair<string, SortedDictionary<int, Sprite>> group in frameGroups)
        {
            List<Sprite> frames = new(group.Value.Values);
            // 只有一帧的多半是误命名，仍按静态表情收下，别让它从面板里消失
            float fps = frames.Count > 1 ? ReadFps(group.Key) : DefaultFps;
            if (frames.Count > 1) animatedCount++;
            AddSticker(group.Key, frames.ToArray(), fps);
        }

        // 固定顺序：按键名排序，保证面板顺序稳定（换会话/重进游戏都一样）
        all.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));

        Debug.Log($"[表情]目录加载完成：{all.Count} 个表情（其中动画 {animatedCount} 个），目录 Resources/{ResourceFolder}");
    }

    /// <summary>登记一个表情（同名键后到的覆盖先到的，避免半成品资源把已有表情顶掉）</summary>
    private static void AddSticker(string key, Sprite[] frames, float fps)
    {
        if (string.IsNullOrEmpty(key) || frames == null || frames.Length == 0) return;

        StickerInfo info = new()
        {
            Key = key,
            Frames = frames,
            Fps = fps,
        };
        byKey[key] = info;

        int existing = all.FindIndex(item => item.Key == key);
        if (existing >= 0) all[existing] = info;
        else all.Add(info);
    }

    /// <summary>
    /// 读取动画帧率的方法（帧目录里的 sticker.txt，形如 "fps=16.67"）
    /// 读不到或格式不对就用默认帧率——表情照常播放，不因为缺个文本文件就不显示
    /// </summary>
    private static float ReadFps(string key)
    {
        TextAsset meta = Resources.Load<TextAsset>($"{ResourceFolder}/{key}/sticker");
        if (meta == null) return DefaultFps;

        foreach (string line in meta.text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("fps=", StringComparison.OrdinalIgnoreCase)) continue;
            if (float.TryParse(trimmed.Substring(4).Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float fps) && fps > 0.01f)
            {
                return Mathf.Clamp(fps, 1f, 60f);
            }
        }
        return DefaultFps;
    }
}
