using UnityEngine;

/// <summary>
/// UI 主题常量（原神风浅色手绘）。数值与 <c>Assets/Game/Resources/ui/Theme</c> 里的底图配套。
/// </summary>
public static class UITheme
{
    public static readonly Color TextMain = new Color32(0x43, 0x40, 0x3A, 0xFF);
    public static readonly Color TextSub = new Color32(0x8C, 0x85, 0x78, 0xFF);
    public static readonly Color Gold = new Color32(0xD9, 0xB2, 0x5C, 0xFF);
    public static readonly Color PanelTint = Color.white;
    public static readonly Color RowTint = new Color32(0xEF, 0xE7, 0xD6, 0xFF);
    public static readonly Color RowTintStrong = new Color32(0xE4, 0xD9, 0xC2, 0xFF);
    public static readonly Color Backdrop = new Color(0f, 0f, 0f, 0.45f);

    /// <summary>Resources 里的主题贴图目录（工程里实际目录是小写 ui）</summary>
    public const string ThemeResourceDir = "ui/Theme";

    public static Sprite LoadPanelBackground() => Resources.Load<Sprite>(ThemeResourceDir + "/panel_bg");

    /// <summary>面板专属底图（bg_&lt;key&gt;）；键为空或图不存在时返回 null，由调用方退回通用底图</summary>
    public static Sprite LoadBackground(string key) =>
        string.IsNullOrEmpty(key) ? null : Resources.Load<Sprite>(ThemeResourceDir + "/bg_" + key);

    public static Sprite LoadBanner(string key) =>
        string.IsNullOrEmpty(key) ? null : Resources.Load<Sprite>(ThemeResourceDir + "/banner_" + key);

    /// <summary>
    /// 面板四周的虚化底：主页插画的模糊版（blur_home）。
    /// 用于非窗口面板——框架会把下层（主页）隐藏，没有它就会露出裸 3D 场景。
    /// </summary>
    public static Sprite LoadHomeBackdrop() => Resources.Load<Sprite>(ThemeResourceDir + "/blur_home");
    public static Sprite LoadRounded() => Resources.Load<Sprite>(ThemeResourceDir + "/panel_round");
    public static Sprite LoadButton() => Resources.Load<Sprite>(ThemeResourceDir + "/btn_round");
    public static Sprite LoadCircle() => Resources.Load<Sprite>(ThemeResourceDir + "/circle");

    /// <summary>相对亮度（用于把深色底/浅色字识别出来）</summary>
    public static float Luminance(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

    /// <summary>饱和度（0 灰 → 1 满色）</summary>
    public static float Saturation(Color c)
    {
        float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        float min = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
        return max <= 0.0001f ? 0f : (max - min) / max;
    }
}
