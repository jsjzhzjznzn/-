using TMPro;

using UnityEngine;

/// <summary>
/// 中文字体定位（运行时程序化创建的 TMP 文本用）。
///
/// 动态创建的 TMP 文本默认继承 TMP Settings 的默认字体（LiberationSans），没有中文字形，
/// 表现就是整段变成方块「□□□」。这里按可靠性顺序找一个能显示「中」的字体：
/// 1) Resources 下的项目字体（若被放进 Resources 则优先，路径固定、与场景无关）；
/// 2) TMP Settings 默认字体（若项目已把默认字体换成了中文字体）；
/// 3) 场景里已存在且能显示「中」的 TMP 文本（登录页/主页一定有中文文本，借它最稳）。
/// 结果缓存：字体是资产，不会随场景卸载失效。
/// </summary>
public static class ChineseFontLocator
{
    /// <summary>约定路径：把中文字体放到 Resources/Fonts/ 下即可被优先取到</summary>
    public const string ResourcePath = "Fonts/NotoSansSC-Regular SDF";

    private static TMP_FontAsset cached;
    private static bool resolved;

    /// <summary>取一个能显示中文的字体（找不到返回 null，调用方保留默认字体）</summary>
    public static TMP_FontAsset Get()
    {
        if (resolved) return cached;
        if (cached != null && cached.HasCharacter('中')) return cached;

        cached = Resolve();
        resolved = cached != null;
        return cached;
    }

    /// <summary>给目标文本套上中文字体（拿不到就原样返回）</summary>
    public static void Apply(TMP_Text target)
    {
        if (target == null) return;
        if (target.font != null && target.font.HasCharacter('中')) return;

        TMP_FontAsset font = Get();
        if (font != null) target.font = font;
    }

    private static TMP_FontAsset Resolve()
    {
        TMP_FontAsset resource = Resources.Load<TMP_FontAsset>(ResourcePath);
        if (resource != null && resource.HasCharacter('中')) return resource;

        if (TMP_Settings.defaultFontAsset != null && TMP_Settings.defaultFontAsset.HasCharacter('中'))
            return TMP_Settings.defaultFontAsset;

        foreach (TMP_Text candidate in Object.FindObjectsOfType<TMP_Text>(true))
        {
            TMP_FontAsset font = candidate.font;
            if (font != null && font.HasCharacter('中')) return font;
        }
        return null;
    }

    /// <summary>清缓存（编辑器热重载调试用；正常不需要调用）</summary>
    public static void Reset()
    {
        cached = null;
        resolved = false;
    }
}
