using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 面板换肤（原神风浅色手绘）：挂在视图根节点上，开面板时把面板底换成浅色底图、
/// 顶部叠一条主题横幅，并把原本"深色底 + 浅色字"的配色整体转成"浅色底 + 深色字"。
///
/// 只在运行期改运行时实例的颜色/Sprite，**不改预制体原始数据**，
/// 所以随时可以摘掉本组件回到旧皮肤；也不会碰业务逻辑与 UIControlData 绑定。
/// </summary>
public class PanelSkin : MonoBehaviour
{
    /// <summary>面板底图键名（对应 Resources/ui/Theme/bg_&lt;key&gt;.png）；留空则用通用 panel_bg</summary>
    [SerializeField] private string backgroundKey = string.Empty;

    /// <summary>顶部横幅的键名（对应 Resources/ui/Theme/banner_&lt;key&gt;.png）；留空则不画横幅</summary>
    [SerializeField] private string bannerKey = string.Empty;

    /// <summary>横幅高度（面板本地单位，700x620 面板建议 110）</summary>
    [SerializeField] private float bannerHeight = 110f;

    /// <summary>是否做浅色化（关掉只换底图与横幅）</summary>
    [SerializeField] private bool applyLightTheme = true;

    /// <summary>
    /// 是否在面板后方铺一层同色系虚化底。非窗口（isWindow=false）面板会把下层主页隐藏，
    /// 不铺的话四周就是裸 3D 场景（蓝天＋灰地板）；窗口型面板浮在主页上，应关掉。
    /// </summary>
    [SerializeField] private bool applyBackdrop = true;

    /// <summary>虚化底的压暗色（乘算）：保留整个主页世界的暖色，只压一档亮度做景深</summary>
    [SerializeField] private Color backdropTint = new Color(0.80f, 0.78f, 0.74f, 1f);

    private bool _applied;
    private Image _panelImage;

    private void OnEnable()
    {
        Apply();
    }

    /// <summary>幂等：重复调用不会重复加横幅/重复改色</summary>
    public void Apply()
    {
        if (_applied) return;
        _applied = true;

        RectTransform panel = FindPanel();
        if (panel == null)
        {
            Debug.LogWarning("[PanelSkin] 找不到面板节点，跳过换肤：" + name);
            return;
        }

        bool hasLightBackground = ApplyPanelBackground(panel);
        ApplyBanner(panel);
        EnsureBackdrop();

        // 底图缺失时**不做**浅色化：否则深色面板上会出现深色字，直接不可读
        if (applyLightTheme && hasLightBackground)
        {
            Recolor(panel);
            FixDisabledButtonContrast(panel);
        }
    }

    private RectTransform FindPanel()
    {
        // 约定优先：面板节点名以 Panel 结尾（FriendPanel/MailPanel/AgentPanel/Panel...）。
        // 不能"跳过带 Canvas 的节点"——好友/阿福面板自带嵌套 Canvas（聊天输入框/CanvasGroup），
        // 那样会误选到全屏根节点或 Backdrop，把底图铺满整屏。
        RectTransform largest = null;
        float largestArea = -1f;

        for (int i = 0; i < transform.childCount; i++)
        {
            var child = transform.GetChild(i) as RectTransform;
            if (child == null) continue;

            string childName = child.name;
            if (childName.EndsWith("Panel")) return child;
            if (childName == "Backdrop" || childName.StartsWith("Skin")) continue;

            float area = Mathf.Abs(child.rect.width * child.rect.height);
            if (area > largestArea)
            {
                largestArea = area;
                largest = child;
            }
        }

        return largest != null ? largest : transform as RectTransform;
    }

    private bool ApplyPanelBackground(RectTransform panel)
    {
        // 优先用面板专属底图（bg_friend/bg_mail/bg_bag…），没有就退回通用羊皮纸底
        Sprite bg = UITheme.LoadBackground(backgroundKey);
        if (bg == null) bg = UITheme.LoadPanelBackground();
        if (bg == null) return false;

        _panelImage = panel.GetComponent<Image>();
        if (_panelImage == null) _panelImage = panel.gameObject.AddComponent<Image>();
        _panelImage.sprite = bg;
        // 底图尺寸与面板尺寸不保证同比例（好友/邮件 700x620，背包 1440x800），
        // 且 AI 底图四角有装饰、不是透明圆角——整图拉伸最稳，切九宫格会把装饰拉变形。
        _panelImage.type = Image.Type.Simple;
        _panelImage.color = Color.white;
        return true;
    }

    /// <summary>
    /// 在视图根节点的**最底层**铺一张全屏虚化底（面板底图的模糊版）。
    /// 视图自带 Backdrop（设置/大厅的遮罩）时不动它，避免叠两层。
    /// </summary>
    private void EnsureBackdrop()
    {
        if (!applyBackdrop) return;
        if (transform.Find("SkinBackdrop") != null) return;
        if (transform.Find("Backdrop") != null) return;

        Sprite bg = UITheme.LoadHomeBackdrop();

        var go = new GameObject("SkinBackdrop", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(transform, false);
        // 必须是第一层：uGUI 按层级顺序绘制，面板在它之后
        rt.SetAsFirstSibling();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var img = go.AddComponent<Image>();
        img.type = Image.Type.Simple;
        img.sprite = bg;
        img.color = bg != null ? backdropTint : new Color(0.24f, 0.23f, 0.21f, 1f);
        // 面板外的点击由它吃掉：下层视图此时已被框架隐藏，不该透给 3D 或更下层
        img.raycastTarget = true;
    }

    private void ApplyBanner(RectTransform panel)
    {
        Sprite banner = UITheme.LoadBanner(bannerKey);
        if (banner == null) return;

        // 横幅容器只做裁切：原图 1088x608（1.79:1）直接压到 700x110（6.36:1）会把画面
        // 纵向压扁、飞鸟翅膀被切掉，所以按"等比放大到铺满容器 → 居中裁切"来摆
        var crop = new GameObject("SkinBannerCrop", typeof(RectTransform), typeof(RectMask2D));
        var cropRt = (RectTransform)crop.transform;
        cropRt.SetParent(panel, false);
        cropRt.SetAsFirstSibling();
        cropRt.anchorMin = new Vector2(0f, 1f);
        cropRt.anchorMax = new Vector2(1f, 1f);
        cropRt.pivot = new Vector2(0.5f, 1f);
        cropRt.anchoredPosition = Vector2.zero;
        cropRt.sizeDelta = new Vector2(0f, bannerHeight);

        var go = new GameObject("SkinBanner", typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(cropRt, false);
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        // 面板刚激活时 rect 可能还没算出来（0 宽），退化成按高度撑开，下一帧布局就位后仍是等比
        float viewWidth = panel.rect.width > 1f ? panel.rect.width : bannerHeight * (banner.rect.width / Mathf.Max(1f, banner.rect.height));
        rt.sizeDelta = CoverSize(banner, viewWidth, bannerHeight);

        var img = go.GetComponent<Image>();
        img.sprite = banner;
        img.type = Image.Type.Simple;
        img.raycastTarget = false;

        // 横幅底部一条金色装饰线，和内容区分开
        var line = new GameObject("SkinBannerLine", typeof(RectTransform), typeof(Image));
        var lineRt = (RectTransform)line.transform;
        lineRt.SetParent(panel, false);
        lineRt.SetSiblingIndex(1);
        lineRt.anchorMin = new Vector2(0f, 1f);
        lineRt.anchorMax = new Vector2(1f, 1f);
        lineRt.pivot = new Vector2(0.5f, 1f);
        lineRt.anchoredPosition = new Vector2(0f, -bannerHeight);
        lineRt.sizeDelta = new Vector2(-32f, 2f);
        var lineImg = line.GetComponent<Image>();
        lineImg.color = UITheme.Gold;
        lineImg.raycastTarget = false;
    }

    /// <summary>cover 尺寸：等比放大到刚好铺满视口，多出来的部分交给 RectMask2D 裁掉</summary>
    private static Vector2 CoverSize(Sprite sprite, float viewWidth, float viewHeight)
    {
        if (sprite == null || viewWidth <= 0f || viewHeight <= 0f) return new Vector2(viewWidth, viewHeight);

        float spriteAspect = sprite.rect.width / Mathf.Max(1f, sprite.rect.height);
        float viewAspect = viewWidth / viewHeight;
        return viewAspect >= spriteAspect
            ? new Vector2(viewWidth, viewWidth / spriteAspect)   // 视口更宽 → 按宽度铺满，上下裁
            : new Vector2(viewHeight * spriteAspect, viewHeight); // 视口更高 → 按高度铺满，左右裁
    }

    /// <summary>把浅色字/深色纯色块整体翻成浅色主题；带颜色的图标、稀有度色、纯色徽章一律跳过</summary>
    private void Recolor(RectTransform panel)
    {
        foreach (var text in panel.GetComponentsInChildren<TextMeshProUGUI>(true))
        {
            if (text == null) continue;
            if (HasSaturatedBadgeBehind(text.transform, panel)) continue;

            Color c = text.color;
            float lum = UITheme.Luminance(c);
            if (lum < 0.42f || c.a < 0.25f) continue;

            if (UITheme.Saturation(c) > 0.30f) continue;   // 金色/蓝色强调字保持原样

            text.color = lum > 0.60f ? UITheme.TextMain : UITheme.TextSub;
        }

        foreach (var img in panel.GetComponentsInChildren<Image>(true))
        {
            if (img == null || img == _panelImage) continue;
            if (img.sprite != null) continue;             // 图标/头像/横幅不动
            if (img.transform.name == "SkinBannerLine") continue;

            Color c = img.color;
            if (c.a < 0.12f) continue;
            if (UITheme.Luminance(c) > 0.32f) continue;   // 只翻深色块
            if (UITheme.Saturation(c) > 0.55f) continue;  // 高饱和色块（按钮/标签）不动

            float a = Mathf.Clamp01(c.a);
            img.color = new Color(UITheme.RowTint.r, UITheme.RowTint.g, UITheme.RowTint.b, a * 0.92f);
        }
    }

    /// <summary>
    /// 浅色底上按钮禁用态默认是"淡灰半透明"，白字标签直接看不清
    /// （背包未选中物品时的"使用/丢弃"就是这样）。只处理标签为浅色字的按钮：
    /// 把禁用底色压成半透明钢蓝，淡下去但仍托得住白字。
    /// </summary>
    private static void FixDisabledButtonContrast(RectTransform panel)
    {
        foreach (var button in panel.GetComponentsInChildren<Button>(true))
        {
            if (button == null) continue;

            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null || UITheme.Luminance(label.color) < 0.6f) continue;   // 深色字按钮不受影响

            ColorBlock colors = button.colors;
            if (UITheme.Luminance(colors.disabledColor) < 0.55f) continue;          // 禁用底本来就是深色

            colors.disabledColor = new Color(0.34f, 0.37f, 0.41f, 0.8f);
            button.colors = colors;
        }
    }

    /// <summary>
    /// 文字背后若有高饱和度的纯色块（红点、稀有度徽章等），说明是"彩底白字"，不该翻成深色字。
    /// </summary>
    private static bool HasSaturatedBadgeBehind(Transform node, Transform stopAt)
    {
        Transform t = node.parent;
        while (t != null && t != stopAt)
        {
            var img = t.GetComponent<Image>();
            if (img != null && img.sprite == null && UITheme.Saturation(img.color) > 0.35f
                && UITheme.Luminance(img.color) < 0.55f)
            {
                return true;
            }
            t = t.parent;
        }
        return false;
    }
}
