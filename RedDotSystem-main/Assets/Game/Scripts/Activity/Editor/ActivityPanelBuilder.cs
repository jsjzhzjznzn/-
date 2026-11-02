using System.IO;

using TMPro;

using UnityEditor;

using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 活动中心面板预制体构建器（编辑器专用，可重复执行）
///
/// 分工：**本文件负责"长什么样"**（层级 / 命名 / 版式 / 配色，产物是热更包里的 prefab），
///       **热更层的 Lua 负责"怎么动"**（侧栏列表、切页、点击、倒计时、抽卡/签到回执、列表填充）。
/// 因此这里的节点名是双方契约，改名等于改 API —— Lua 侧按名取节点（ActivityUi.Find*/BindButton）。
///
/// 版式（鸣潮式活动中心）：TopBar（选中活动标题+倒计时）+ 左侧 Sidebar（活动栏，Lua 按活动列表克隆）
/// + 右侧互斥内容页 GachaPage（唤取）/ SignInPage（签到）；ResultPage/HistoryPage 仍是唤取的全屏覆盖层。
///
/// 产物：Assets/Content/HotContent/UI/ActivityPanel.prefab（HotContent 是 YooAsset 热更收集目录，按文件名寻址）
/// 用法：菜单 Tools/活动热更/重建活动面板预制体
/// </summary>
public static class ActivityPanelBuilder
{
    public const string PrefabPath = "Assets/Content/HotContent/UI/ActivityPanel.prefab";

    // ── 锚点（= pivot，遵循"角锚配同角 pivot"约定） ──
    private static readonly Vector2 AnchorTopLeft = new Vector2(0f, 1f);
    private static readonly Vector2 AnchorTopCenter = new Vector2(0.5f, 1f);
    private static readonly Vector2 AnchorTopRight = new Vector2(1f, 1f);
    private static readonly Vector2 AnchorMidLeft = new Vector2(0f, 0.5f);
    private static readonly Vector2 AnchorMiddle = new Vector2(0.5f, 0.5f);
    private static readonly Vector2 AnchorMidRight = new Vector2(1f, 0.5f);
    private static readonly Vector2 AnchorBottomLeft = new Vector2(0f, 0f);
    private static readonly Vector2 AnchorBottomCenter = new Vector2(0.5f, 0f);
    private static readonly Vector2 AnchorBottomRight = new Vector2(1f, 0f);

    // ── 配色（鸣潮风深色 HUD） ──
    private static Color Hex(string hex, float alpha = 1f)
    {
        string value = hex.TrimStart('#');
        return new Color(
            System.Convert.ToInt32(value.Substring(0, 2), 16) / 255f,
            System.Convert.ToInt32(value.Substring(2, 2), 16) / 255f,
            System.Convert.ToInt32(value.Substring(4, 2), 16) / 255f,
            alpha);
    }

    private static readonly Color Deep = Hex("#0C141B");
    private static readonly Color Card = Hex("#1E2A35");
    private static readonly Color Slate = Hex("#2C3E4E");
    private static readonly Color Gold = Hex("#E8C46A");
    private static readonly Color Blue = Hex("#7FB2D9");
    private static readonly Color TextMain = Hex("#E6EDF3");
    private static readonly Color TextDim = Hex("#8A9AA8");
    private static readonly Color Red = Hex("#B04A4A");

    private static Sprite panelSprite;

    [MenuItem("Tools/活动热更/重建活动面板预制体")]
    public static void BuildFromMenu()
    {
        Build();
    }

    /// <summary>构建并保存预制体（返回保存路径；失败返回 null）</summary>
    public static string Build()
    {
        panelSprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");

        string directory = Path.GetDirectoryName(PrefabPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        GameObject root = new GameObject("ActivityPanel", typeof(RectTransform), typeof(CanvasGroup));
        RectTransform rootRect = (RectTransform)root.transform;
        StretchFull(rootRect);

        BuildBackground(rootRect);
        BuildTopBar(rootRect);
        BuildSidebar(rootRect);
        BuildGachaPage(rootRect);
        BuildSignInPage(rootRect);
        BuildToast(rootRect);
        BuildLoading(rootRect);
        BuildResultPage(rootRect);
        BuildHistoryPage(rootRect);

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);

        if (saved == null)
        {
            Debug.LogError($"[活动] 预制体保存失败：{PrefabPath}");
            return null;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[活动] 唤取面板预制体已生成：{PrefabPath}（内置 UI 精灵：{(panelSprite != null ? panelSprite.name + " border=" + panelSprite.border : "缺失")}）");
        return PrefabPath;
    }

    // ──────────────────────────────
    // 各分区
    // ──────────────────────────────

    private static void BuildBackground(RectTransform root)
    {
        Image background = Img(root, "Background", Color.white, null, false, true);
        background.preserveAspect = false;
        background.raycastTarget = true;                       // 全屏面板：挡住下层场景 UI（模态）

        Img(root, "Shade", Hex("#0C141B", 0.42f), null, false, true);

        Image band = Img(root, "BottomBand", Hex("#0C141B", 0.55f), null, false, false);
        band.rectTransform.anchorMin = new Vector2(0f, 0f);
        band.rectTransform.anchorMax = new Vector2(1f, 0f);
        band.rectTransform.pivot = new Vector2(0.5f, 0f);
        band.rectTransform.sizeDelta = new Vector2(0f, 320f);
        band.rectTransform.anchoredPosition = Vector2.zero;
    }

    private static void BuildTopBar(RectTransform root)
    {
        RectTransform bar = Rect(root, "TopBar", AnchorTopCenter, AnchorTopCenter, Vector2.zero, new Vector2(0f, 140f));
        bar.anchorMin = new Vector2(0f, 1f);
        bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 1f);
        bar.anchoredPosition = Vector2.zero;
        bar.sizeDelta = new Vector2(0f, 140f);

        // 标题/副标题 = 当前选中活动（切换侧栏活动时由 Lua 更新文本）
        Label(bar, "Title", "活动中心", 46, TextMain, TextAlignmentOptions.MidlineLeft, AnchorTopLeft,
            new Vector2(56f, -26f), new Vector2(900f, 64f), true);
        Label(bar, "Subtitle", "活动副标题", 24, TextDim, TextAlignmentOptions.MidlineLeft, AnchorTopLeft,
            new Vector2(58f, -94f), new Vector2(900f, 36f));
        Label(bar, "Countdown", "剩余 --", 28, Blue, TextAlignmentOptions.MidlineRight, AnchorTopRight,
            new Vector2(-136f, -28f), new Vector2(520f, 56f), true);

        Button close = Btn(bar, "CloseButton", "×", 40, TextMain, Red, new Vector2(64f, 64f));
        AnchorTo(close.GetComponent<RectTransform>(), AnchorTopRight, new Vector2(-40f, -24f));

        Label(root, "VersionTag", "内容版本 -", 20, Hex("#5A6B78"), TextAlignmentOptions.MidlineLeft, AnchorBottomLeft,
            new Vector2(296f, 16f), new Vector2(560f, 28f));
    }

    /// <summary>
    /// 左侧活动栏（鸣潮式活动中心）：一个按钮模板，Lua 按服务器下发的活动列表克隆
    /// （选中态 = Lua 改 ButtonBg 颜色 + Label 颜色；节点名是双方契约）
    /// </summary>
    private static void BuildSidebar(RectTransform root)
    {
        RectTransform sidebar = Rect(root, "Sidebar", AnchorBottomLeft, AnchorBottomLeft, Vector2.zero, Vector2.zero);
        sidebar.anchorMin = new Vector2(0f, 0f);
        sidebar.anchorMax = new Vector2(0f, 1f);
        sidebar.pivot = new Vector2(0f, 0.5f);
        sidebar.offsetMin = new Vector2(0f, 0f);
        sidebar.offsetMax = new Vector2(260f, -140f);   // 左列 260px，TopBar（140px）之下到底

        Image background = Img(sidebar, "SidebarBg", Hex("#16222C", 0.86f), panelSprite, true, true);
        StretchFull(background.rectTransform);

        RectTransform buttonTemplate = Rect(sidebar, "SidebarButtonTemplate", AnchorTopCenter, AnchorTopCenter,
            new Vector2(0f, -16f), new Vector2(220f, 84f));
        Image buttonBg = Img(buttonTemplate, "ButtonBg", Hex("#233241"), panelSprite, true, true);
        StretchFull(buttonBg.rectTransform);

        Label(buttonTemplate, "Label", "活动名", 26, TextMain, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, 0f), new Vector2(204f, 44f));

        Button button = buttonTemplate.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonBg;
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        buttonTemplate.gameObject.SetActive(false);   // 模板：Lua 克隆后激活
    }

    /// <summary>唤取内容页（活动中心的一"页"；LeftInfo/RightActions 从根挪进来并右移让出侧栏）</summary>
    private static void BuildGachaPage(RectTransform root)
    {
        RectTransform page = Rect(root, "GachaPage", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(page);

        BuildLeftInfo(page);
        BuildRightActions(page);
    }

    private static void BuildLeftInfo(RectTransform parent)
    {
        RectTransform area = Rect(parent, "LeftInfo", AnchorBottomLeft, AnchorBottomLeft, new Vector2(316f, 44f), new Vector2(520f, 240f));

        Label(area, "PityTitle", "5★ 保底进度", 22, TextDim, TextAlignmentOptions.MidlineLeft, AnchorBottomLeft,
            new Vector2(0f, 190f), new Vector2(420f, 30f));
        Label(area, "PityText", "0 / 30", 32, Gold, TextAlignmentOptions.MidlineLeft, AnchorBottomLeft,
            new Vector2(0f, 148f), new Vector2(420f, 42f), true);

        Image barBg = Img(area, "PityBarBg", Hex("#22303C"), panelSprite, true, false);
        AnchorTo(barBg.rectTransform, AnchorBottomLeft, new Vector2(0f, 112f));
        barBg.rectTransform.sizeDelta = new Vector2(420f, 18f);

        Image fill = Img(barBg.rectTransform, "PityBarFill", Gold, panelSprite, true, false);
        AnchorTo(fill.rectTransform, AnchorBottomLeft, Vector2.zero);
        fill.rectTransform.sizeDelta = new Vector2(0f, 18f);

        Image icon = Img(area, "MediaIcon", Color.white, null, false, false);
        AnchorTo(icon.rectTransform, AnchorBottomLeft, Vector2.zero);
        icon.rectTransform.sizeDelta = new Vector2(64f, 64f);
        icon.preserveAspect = true;

        Label(area, "MediaText", "唤取媒介 ×0", 26, TextMain, TextAlignmentOptions.MidlineLeft, AnchorBottomLeft,
            new Vector2(80f, 10f), new Vector2(420f, 44f));
    }

    private static void BuildRightActions(RectTransform parent)
    {
        RectTransform area = Rect(parent, "RightActions", AnchorBottomRight, AnchorBottomRight, new Vector2(-56f, 44f), new Vector2(900f, 280f));

        Button ten = Btn(area, "BtnDrawTen", "唤取十次", 36, Deep, Gold, new Vector2(400f, 120f));
        AnchorTo(ten.GetComponent<RectTransform>(), AnchorBottomRight, Vector2.zero);
        Label(ten.transform as RectTransform, "Cost", "消耗 10 媒介", 20, Hex("#5C4A20"), TextAlignmentOptions.Center, AnchorBottomCenter,
            new Vector2(0f, 14f), new Vector2(360f, 26f));

        Button one = Btn(area, "BtnDrawOne", "唤取一次", 30, TextMain, Slate, new Vector2(300f, 120f));
        AnchorTo(one.GetComponent<RectTransform>(), AnchorBottomRight, new Vector2(-424f, 0f));
        Label(one.transform as RectTransform, "Cost", "消耗 1 媒介", 20, Hex("#AFBECB"), TextAlignmentOptions.Center, AnchorBottomCenter,
            new Vector2(0f, 14f), new Vector2(280f, 26f));

        Button history = Btn(area, "BtnHistory", "唤取详单", 26, TextDim, Card, new Vector2(240f, 64f));
        AnchorTo(history.GetComponent<RectTransform>(), AnchorBottomRight, new Vector2(0f, 144f));
    }

    /// <summary>
    /// 签到内容页（鸣潮式 7 天累计签到）：
    ///   SignInSummary 进度摘要 / DayArea+DayCellTemplate 七日奖励格（Lua 克隆）/ BtnSignIn 签到大按钮
    /// 格子的状态配色与呼吸光全部由 Lua 驱动（数据来自服务端签到快照）
    /// </summary>
    private static void BuildSignInPage(RectTransform root)
    {
        RectTransform page = Rect(root, "SignInPage", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(page);

        Label(page, "SignInSummary", "已签到 0 / 7 天", 34, Gold, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -212f), new Vector2(900f, 48f), true);

        RectTransform dayArea = Rect(page, "DayArea", AnchorMiddle, AnchorMiddle, new Vector2(0f, 100f), new Vector2(1620f, 300f));

        RectTransform cell = Rect(dayArea, "DayCellTemplate", AnchorMiddle, AnchorMiddle, Vector2.zero, new Vector2(200f, 260f));

        Image glow = Img(cell, "Glow", Hex("#E8C46A", 0.32f), null, false, false);
        AnchorTo(glow.rectTransform, AnchorMiddle, Vector2.zero);
        glow.rectTransform.sizeDelta = new Vector2(280f, 320f);   // sprite 由 Lua 换成 SoftGlow（呼吸光）

        Image cellBg = Img(cell, "CellBg", Card, panelSprite, true, false);
        StretchFull(cellBg.rectTransform);

        Label(cell, "DayBadge", "第1天", 22, Gold, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -14f), new Vector2(160f, 32f), true);

        Image icon = Img(cell, "Icon", Color.white, null, false, false);
        AnchorTo(icon.rectTransform, AnchorMiddle, new Vector2(0f, 8f));
        icon.rectTransform.sizeDelta = new Vector2(128f, 128f);
        icon.preserveAspect = true;

        Label(cell, "CountText", "×2", 24, TextMain, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, -70f), new Vector2(160f, 34f));

        Label(cell, "StateText", "未解锁", 20, TextDim, TextAlignmentOptions.Center, AnchorBottomCenter,
            new Vector2(0f, 16f), new Vector2(170f, 30f));

        Label(cell, "DoneMark", "已领", 20, Gold, TextAlignmentOptions.Center, AnchorTopRight,
            new Vector2(-10f, -12f), new Vector2(56f, 28f), true);

        cell.gameObject.SetActive(false);   // 模板：Lua 按服务端下发的天数克隆

        Button signIn = Btn(page, "BtnSignIn", "签 到", 34, Deep, Gold, new Vector2(360f, 96f));
        AnchorTo(signIn.GetComponent<RectTransform>(), AnchorBottomCenter, new Vector2(0f, 110f));

        page.gameObject.SetActive(false);   // 默认隐藏；选中签到活动时由 Lua 打开
    }

    private static void BuildToast(RectTransform root)
    {
        Label(root, "Toast", "", 28, Hex("#FFFFFF", 0f), TextAlignmentOptions.Center, AnchorBottomCenter,
            new Vector2(0f, 320f), new Vector2(1400f, 50f));
    }

    private static void BuildLoading(RectTransform root)
    {
        RectTransform loading = Rect(root, "Loading", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(loading);

        Image dim = Img(loading, "Dim", Hex("#0C141B", 0.62f), null, false, true);
        dim.raycastTarget = true;

        Image ring = Img(loading, "Ring", Gold, null, false, false);
        AnchorTo(ring.rectTransform, AnchorMiddle, new Vector2(0f, 36f));
        ring.rectTransform.sizeDelta = new Vector2(110f, 110f);

        Label(loading, "LoadingText", "唤取中…", 30, TextMain, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, -70f), new Vector2(460f, 50f));

        loading.gameObject.SetActive(false);
    }

    private static void BuildResultPage(RectTransform root)
    {
        RectTransform page = Rect(root, "ResultPage", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(page);

        Image dim = Img(page, "Dim", Hex("#0C141B", 0.9f), null, false, true);
        dim.raycastTarget = true;

        Label(page, "ResultTitle", "唤取结果", 44, Gold, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -60f), new Vector2(900f, 60f), true);
        Label(page, "ResultHint", "", 24, TextDim, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -124f), new Vector2(900f, 36f));

        // 卡牌容器：格子位置由 Lua 摆；模板 inactive，等 Lua 克隆
        RectTransform cardArea = Rect(page, "CardArea", AnchorMiddle, AnchorMiddle, new Vector2(0f, 30f), new Vector2(1700f, 760f));
        RectTransform card = Rect(cardArea, "CardTemplate", AnchorMiddle, AnchorMiddle, Vector2.zero, new Vector2(300f, 360f));

        Image glow = Img(card, "Glow", Hex("#E8C46A", 0.30f), null, false, false);
        AnchorTo(glow.rectTransform, AnchorMiddle, Vector2.zero);
        glow.rectTransform.sizeDelta = new Vector2(460f, 460f);

        Image frame = Img(card, "Frame", Card, panelSprite, true, false);
        StretchFull(frame.rectTransform);

        Image icon = Img(card, "Icon", Color.white, null, false, false);
        AnchorTo(icon.rectTransform, AnchorMiddle, new Vector2(0f, 26f));
        icon.rectTransform.sizeDelta = new Vector2(200f, 230f);
        icon.preserveAspect = true;

        Label(card, "Stars", "★", 30, Gold, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, -104f), new Vector2(280f, 40f), true);
        Label(card, "Name", "物品名", 26, TextMain, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, -152f), new Vector2(280f, 44f));
        Label(card, "BadgeNew", "新", 24, Gold, TextAlignmentOptions.Center, AnchorTopRight,
            new Vector2(-14f, -14f), new Vector2(90f, 40f));
        Label(card, "BadgePity", "保底", 22, Blue, TextAlignmentOptions.Center, AnchorTopLeft,
            new Vector2(14f, -14f), new Vector2(110f, 40f));

        card.gameObject.SetActive(false);

        Button confirm = Btn(page, "BtnConfirm", "确认", 32, Deep, Gold, new Vector2(320f, 96f));
        AnchorTo(confirm.GetComponent<RectTransform>(), AnchorBottomCenter, new Vector2(0f, 80f));

        page.gameObject.SetActive(false);
    }

    private static void BuildHistoryPage(RectTransform root)
    {
        RectTransform page = Rect(root, "HistoryPage", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(page);

        Image dim = Img(page, "Dim", Hex("#0C141B", 0.93f), null, false, true);
        dim.raycastTarget = true;

        Label(page, "HistoryTitle", "唤取详单", 44, Gold, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -56f), new Vector2(900f, 60f), true);
        Label(page, "HistorySummary", "", 24, TextDim, TextAlignmentOptions.Center, AnchorTopCenter,
            new Vector2(0f, -118f), new Vector2(900f, 36f));

        // 三段式滚动区：ScrollRect → Viewport(RectMask2D) → Content
        RectTransform scrollRect = Rect(page, "HistoryScroll", AnchorMiddle, AnchorMiddle, new Vector2(0f, 10f), new Vector2(1440f, 640f));
        ScrollRect scroll = scrollRect.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 40f;
        scroll.inertia = false;

        RectTransform viewport = Rect(scrollRect, "Viewport", AnchorMiddle, AnchorMiddle, Vector2.zero, Vector2.zero);
        StretchFull(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();

        RectTransform content = Rect(viewport, "Content", AnchorTopCenter, AnchorTopCenter, Vector2.zero, new Vector2(0f, 640f));
        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);

        scroll.viewport = viewport;
        scroll.content = content;

        RectTransform row = Rect(content, "RowTemplate", AnchorTopCenter, AnchorTopCenter, Vector2.zero, new Vector2(0f, 64f));
        row.anchorMin = new Vector2(0f, 1f);
        row.anchorMax = new Vector2(1f, 1f);
        row.pivot = new Vector2(0.5f, 1f);

        Image rowBg = Img(row, "RowBg", Hex("#16212B", 0.92f), panelSprite, true, false);
        StretchFull(rowBg.rectTransform);

        Label(row, "RowTime", "时间", 22, TextDim, TextAlignmentOptions.MidlineLeft, AnchorMidLeft,
            new Vector2(24f, 0f), new Vector2(300f, 44f));
        Label(row, "RowStars", "★", 26, Gold, TextAlignmentOptions.MidlineLeft, AnchorMidLeft,
            new Vector2(340f, 0f), new Vector2(200f, 44f), true);
        Label(row, "RowName", "物品名", 26, TextMain, TextAlignmentOptions.MidlineLeft, AnchorMidLeft,
            new Vector2(560f, 0f), new Vector2(560f, 44f));
        Label(row, "RowTag", "", 24, Blue, TextAlignmentOptions.MidlineRight, AnchorMidRight,
            new Vector2(-24f, 0f), new Vector2(280f, 44f));

        row.gameObject.SetActive(false);

        Button back = Btn(page, "BtnBack", "返回", 28, TextMain, Slate, new Vector2(260f, 80f));
        AnchorTo(back.GetComponent<RectTransform>(), AnchorBottomCenter, new Vector2(0f, 70f));

        page.gameObject.SetActive(false);
    }

    // ──────────────────────────────
    // 构建小工具
    // ──────────────────────────────

    private static RectTransform Rect(Transform parent, string name, Vector2 anchor, Vector2 pivot, Vector2 position, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    private static void StretchFull(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void AnchorTo(RectTransform rect, Vector2 anchor, Vector2 position)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.anchoredPosition = position;
    }

    private static Image Img(Transform parent, string name, Color color, Sprite sprite, bool sliced, bool raycast)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        StretchFull(rect);

        Image image = go.AddComponent<Image>();
        image.sprite = sprite != null ? sprite : panelSprite;
        image.color = color;
        image.type = sliced ? Image.Type.Sliced : Image.Type.Simple;
        image.raycastTarget = raycast;
        return image;
    }

    private static TextMeshProUGUI Label(Transform parent, string name, string text, int fontSize, Color color,
        TextAlignmentOptions alignment, Vector2 anchor, Vector2 position, Vector2 size, bool bold = false)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = anchor;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = color;
        label.alignment = alignment;
        label.raycastTarget = false;                       // 文本永不挡按钮
        if (bold) label.fontStyle = FontStyles.Bold;
        return label;
    }

    /// <summary>按钮 = 圆角底图 + 居中标签（Label 子物体名固定，便于 Lua 取文本）</summary>
    private static Button Btn(Transform parent, string name, string labelText, int fontSize, Color labelColor,
        Color background, Vector2 size)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = (RectTransform)go.transform;
        rect.SetParent(parent, false);
        rect.localScale = Vector3.one;
        rect.sizeDelta = size;

        Image image = go.AddComponent<Image>();
        image.sprite = panelSprite;
        image.color = background;
        image.type = Image.Type.Sliced;
        image.raycastTarget = true;

        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;
        ColorBlock colors = button.colors;
        colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        Label(rect, "Label", labelText, fontSize, labelColor, TextAlignmentOptions.Center, AnchorMiddle,
            new Vector2(0f, 8f), size);
        return button;
    }
}
