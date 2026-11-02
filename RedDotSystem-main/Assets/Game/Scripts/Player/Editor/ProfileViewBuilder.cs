#if UNITY_EDITOR
using System.Collections.Generic;
using SkierFramework;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// 共鸣者影像簿页（ProfileView）预制体构建器，按参考图复刻：
/// 视频背景 + 左上标题 + 右上拍摄/关闭 + 左侧资料面板（头像/昵称/特征码/签名/信息行/角色卡/编辑）
/// + 右侧选中角色的「影像」（金框照片卡）+ 右下特征码。
///
/// 美术全部在构建期烘进预制体（Sprite 直接引用），运行时不加载资源：
/// 头像与立绘都来自项目内已有图，避免 YooAsset location 不匹配导致白块。
/// 菜单：Tools/UI/重建影像簿界面
/// </summary>
public static class ProfileViewBuilder
{
    private const string PrefabPath = "Assets/Game/UI/Profile/ProfileView.prefab";
    private const string ArtDir = "Assets/Game/UI/Profile/Art";
    /// <summary>暂时用来冒充「影像」的整屏背景图（后续换视频只改这一个常量）</summary>
    private const string BackdropPath = "Assets/Game/Resources/ui/Theme/home_bg.png";
    private const string FontPath = "Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset";

    // 复用已有美术
    private const string CirclePath = "Assets/Game/Resources/ui/Character/avatar_circle.png";
    private const string PillPath = "Assets/Game/Resources/ui/Theme/panel_round.png";
    private const string BarPath = "Assets/Game/Resources/ui/Character/ui_bar.png";
    private const string BgPath = "Assets/Game/Resources/ui/Character/character_bg_haze.jpg";
    private const string PlayerAvatarPath = "Assets/Content/Resource/ui/mingchao/touxiang/T_IconRoleHeadCircle256_1_UI.png";
    private const string MingchaoHeadDir = "Assets/Content/Resource/ui/mingchao/touxiang";

    /// <summary>角色卡头像：鸣潮圆形头像（本身就是裁好的头像图，直接铺满圆框，不用再裁立绘）</summary>
    private static readonly string[] CardHeadPaths =
    {
        MingchaoHeadDir + "/T_IconRoleHead80_1_UI.png",
        MingchaoHeadDir + "/T_IconRoleHead80_2_UI.png",
    };

    private static readonly string[] CardNames = { "安比", "艾莲" };

    // 参考图（1920×1080 画布）量取的关键尺寸：大圆头像占左上，昵称/特征码在头像右侧，
    // 签名条整宽，三张角色卡铺满一行，底部整宽药丸按钮
    private const float PanelLeft = 32f;
    private const float PanelWidth = 620f;
    private const float PanelTop = 92f;
    private const float PanelBottom = 48f;
    private const float PanelPad = 40f;
    private const float AvatarSize = 276f;
    private const float CardWidth = 169f;
    private const float CardHeight = 210f;
    private const float CardGap = 16f;
    private const int CardSlots = 3;

    private static readonly Color Gold = new Color(0.851f, 0.737f, 0.486f, 1f);
    private static readonly Color GoldDim = new Color(0.851f, 0.737f, 0.486f, 0.38f);
    private static readonly Color PanelFill = new Color(0.031f, 0.035f, 0.043f, 0.94f);
    private static readonly Color ChipFill = new Color(1f, 1f, 1f, 0.035f);
    private static readonly Color CardFill = new Color(0.055f, 0.060f, 0.072f, 0.86f);
    private static readonly Color White = new Color(1f, 1f, 1f, 1f);
    private static readonly Color TextMain = new Color(0.94f, 0.93f, 0.91f, 1f);
    private static readonly Color TextSub = new Color(0.70f, 0.70f, 0.72f, 1f);
    private static readonly Color TextDim = new Color(0.52f, 0.53f, 0.56f, 1f);
    private static readonly Color Ink = new Color(0.14f, 0.13f, 0.12f, 1f);

    private static TMP_FontAsset _font;

    private static Sprite _panel;
    private static Sprite _cardFrame;
    private static Sprite _ring;
    private static Sprite _ringDots;
    private static Sprite _line;
    private static Sprite _glow;
    private static Sprite _titleMark;
    private static Sprite _crosshair;
    private static Sprite _iconDots;
    private static Sprite _iconPen;
    private static Sprite _iconCopy;
    private static Sprite _iconInfo;
    private static Sprite _iconEye;
    private static Sprite _circle;
    private static Sprite _pill;
    private static Sprite _bar;
    private static Sprite _playerAvatar;

    [MenuItem("Tools/UI/重建影像簿界面")]
    public static void Build()
    {
        ProfileArt.EnsureAll(ArtDir);
        LoadAssets();

        var wrapper = new GameObject("ProfileView", typeof(RectTransform));
        RectTransform root = wrapper.GetComponent<RectTransform>();
        Stretch(root);

        wrapper.AddComponent<ProfileView>();
        wrapper.AddComponent<UIControlData>();

        BuildBackdrop(wrapper.transform);
        BuildHeader(root);
        RectTransform panel = BuildLeftPanel(root);
        BuildPlayerBlock(panel);
        BuildInfoRows(panel);
        BuildCardRow(panel);
        BuildPanelFooter(panel);
        BuildStage(root);

        wrapper.layer = Layer.UI;
        foreach (Transform child in wrapper.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = Layer.UI;

        ProfileArt.EnsureFolder("Assets/Game/UI/Profile");
        PrefabUtility.SaveAsPrefabAsset(wrapper, PrefabPath);
        Object.DestroyImmediate(wrapper);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void LoadAssets()
    {
        _font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPath);
        _panel = LoadSprite(ArtDir + "/profile_panel.png");
        _cardFrame = LoadSprite(ArtDir + "/card_frame.png");
        _ring = LoadSprite(ArtDir + "/gold_ring.png");
        _ringDots = LoadSprite(ArtDir + "/gold_ring_dots.png");
        _line = LoadSprite(ArtDir + "/gold_line.png");
        _glow = LoadSprite(ArtDir + "/soft_glow.png");
        _titleMark = LoadSprite(ArtDir + "/title_mark.png");
        _crosshair = LoadSprite(ArtDir + "/crosshair.png");
        _iconDots = LoadSprite(ArtDir + "/icon_dots.png");
        _iconPen = LoadSprite(ArtDir + "/icon_pen.png");
        _iconCopy = LoadSprite(ArtDir + "/icon_copy.png");
        _iconInfo = LoadSprite(ArtDir + "/icon_info.png");
        _iconEye = LoadSprite(ArtDir + "/icon_eye.png");
        _circle = LoadSprite(CirclePath);
        _pill = LoadSprite(PillPath);
        _bar = LoadSprite(BarPath);
        _playerAvatar = LoadSprite(PlayerAvatarPath);
    }

    // ──────────────────────────────
    // 背景与顶栏
    // ──────────────────────────────

    /// <summary>视频铺满全屏；底下先垫一层暖金雾面 + 暗压，视频未出画时也不露灰底</summary>
    private static void BuildBackdrop(Transform root)
    {
        Image bg = NewImage(root, "BgArt", new Color(0.9f, 0.85f, 0.78f, 1f), LoadSprite(BgPath));
        Stretch(bg.rectTransform);

        Image dark = NewImage(root, "BgScrim", new Color(0.03f, 0.035f, 0.05f, 0.82f), null);
        Stretch(dark.rectTransform);

        // 视频与播放器同节点：ProfileView 从 VideoPlayer 上取 RawImage
        var go = new GameObject("Backdrop", typeof(RectTransform));
        go.transform.SetParent(root, false);
        EnsureCanvasRenderer(go);
        Stretch((RectTransform)go.transform);
        var backdrop = go.AddComponent<Image>();
        backdrop.sprite = LoadSprite(BackdropPath);
        backdrop.color = Color.white;
        backdrop.raycastTarget = true;

        // 背景上再压一层：亮画面不压暗会把左侧面板和文字全糊掉
        // （不做径向暗角：8 位渐变在整屏尺寸下会出现明显的同心圆带）
        Image scrim = NewImage(root, "VideoScrim", new Color(0.02f, 0.025f, 0.04f, 0.42f), null);
        Stretch(scrim.rectTransform);
    }

    private static void BuildHeader(RectTransform root)
    {
        Image mark = NewImage(root, "TitleMark", White, _titleMark);
        Place(mark.rectTransform, 40f, -28f, 34f, 34f);

        TMP_Text title = NewText("TitleText", root, 30f, White, TextAlignmentOptions.Left);
        Place(title.rectTransform, 84f, -46f, 420f, 44f);
        title.SetText("共鸣者影像簿");
        title.characterSpacing = 4f;
        AddShadow(title);

        Button shoot = NewCircleButton("ShootButton", root, 52f, new Color(1f, 1f, 1f, 0.12f), _circle);
        PlaceRight((RectTransform)shoot.transform, -152f, -26f, 52f, 52f);
        Image shootIcon = NewImage(shoot.transform, "Icon", White, _crosshair);
        Stretch(shootIcon.rectTransform, 12f);

        Button close = NewCircleButton("CloseButton", root, 52f, new Color(1f, 1f, 1f, 0.12f), _circle);
        PlaceRight((RectTransform)close.transform, -34f, -26f, 52f, 52f);
        AddCrossMark(close.transform, White, 20f, 2.6f);
    }

    private static RectTransform BuildLeftPanel(RectTransform root)
    {
        var go = new GameObject("LeftPanel", typeof(RectTransform));
        go.transform.SetParent(root, false);
        EnsureCanvasRenderer(go);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(PanelLeft, PanelBottom);
        rect.offsetMax = new Vector2(PanelLeft + PanelWidth, -PanelTop);

        var bg = go.AddComponent<Image>();
        bg.sprite = _panel;
        bg.type = Image.Type.Sliced;
        bg.color = PanelFill;
        bg.raycastTarget = true;

        return rect;
    }

    // ──────────────────────────────
    // 玩家资料块
    // ──────────────────────────────

    private static void BuildPlayerBlock(RectTransform panel)
    {
        // 大圆头像占左上：圆形遮罩 + 头像 + 金环 + 点环（参考图的香槟金头像圈）
        RectTransform avatar = NewRect("AvatarBlock", panel);
        avatar.anchorMin = avatar.anchorMax = new Vector2(0f, 1f);
        avatar.pivot = new Vector2(0f, 1f);
        avatar.anchoredPosition = new Vector2(PanelPad - 8f, -18f);
        avatar.sizeDelta = new Vector2(AvatarSize, AvatarSize);

        // 遮罩必须与圆形图同节点，被裁的图必须是它的子节点——放成兄弟节点不裁任何东西
        Image maskImage = NewImage(avatar, "AvatarMask", White, _circle);
        Stretch(maskImage.rectTransform);
        maskImage.gameObject.AddComponent<Mask>().showMaskGraphic = false;

        RectTransform playerRect = NewRect("AvatarImage", maskImage.transform);
        Stretch(playerRect);
        EnsureCanvasRenderer(playerRect.gameObject);
        var player = playerRect.gameObject.AddComponent<Image>();
        player.sprite = _playerAvatar;
        player.color = White;
        player.raycastTarget = false;
        player.preserveAspect = true;

        Image ring = NewImage(avatar, "Ring", Gold, _ring);
        Stretch(ring.rectTransform);
        Image dots = NewImage(avatar, "RingDots", new Color(Gold.r, Gold.g, Gold.b, 0.9f), _ringDots);
        Stretch(dots.rectTransform);

        // 头像右侧一列：昵称 / 特征码（参考图这样排，头像不再居中）
        float columnX = PanelPad + AvatarSize + 8f;
        float columnTop = -AvatarSize * 0.62f;

        TMP_Text name = NewText("NameText", panel, 30f, TextMain, TextAlignmentOptions.Left);
        Place(name.rectTransform, columnX, columnTop, 170f, 42f);
        name.SetText("冒险者");
        AddShadow(name);

        Button editName = NewCircleButton("EditNameButton", panel, 34f, new Color(Gold.r, Gold.g, Gold.b, 0.16f), _circle);
        Place((RectTransform)editName.transform, columnX + 178f, columnTop - 3f, 34f, 34f);
        Image penIcon = NewImage(editName.transform, "Icon", Gold, _iconPen);
        Stretch(penIcon.rectTransform, 7f);

        TMP_Text code = NewText("CodeText", panel, 19f, TextSub, TextAlignmentOptions.Left);
        Place(code.rectTransform, columnX, columnTop - 56f, 190f, 30f);
        code.SetText("特征码：--");

        Button copy = NewCircleButton("CopyCodeButton", panel, 32f, new Color(1f, 1f, 1f, 0.06f), _circle);
        Place((RectTransform)copy.transform, columnX + 200f, columnTop - 55f, 32f, 32f);
        Image copyIcon = NewImage(copy.transform, "Icon", new Color(1f, 1f, 1f, 0.7f), _iconCopy);
        Stretch(copyIcon.rectTransform, 7f);

        // 头像右上角的「更多」：参考图里是面板顶部的一个小图标
        Button more = NewCircleButton("MoreButton", panel, 34f, new Color(1f, 1f, 1f, 0.06f), _circle);
        PlaceRight((RectTransform)more.transform, -PanelPad + 2f, -20f, 34f, 34f);
        Image moreIcon = NewImage(more.transform, "Icon", new Color(1f, 1f, 1f, 0.75f), _iconDots);
        Stretch(moreIcon.rectTransform, 9f);

        // 签名条：整宽、加高，压在大头像下方
        float signatureY = -(AvatarSize + 24f);
        RectTransform signature = NewRect("SignatureButton", panel);
        Place(signature, PanelPad, signatureY, PanelWidth - PanelPad * 2f, 62f);
        var signatureBg = NewImage(signature, "Bg", new Color(1f, 1f, 1f, 0.05f), _panel);
        signatureBg.type = Image.Type.Sliced;
        signatureBg.raycastTarget = true;
        Stretch(signatureBg.rectTransform);
        var signatureButton = signature.gameObject.AddComponent<Button>();
        signatureButton.targetGraphic = signatureBg;
        signatureButton.transition = Selectable.Transition.None;

        TMP_Text signatureText = NewText("SignatureText", signature, 19f, new Color(0.55f, 0.56f, 0.58f, 1f), TextAlignmentOptions.Left);
        Stretch(signatureText.rectTransform);
        signatureText.margin = new Vector4(20f, 0f, 20f, 0f);
        signatureText.SetText("点击以编辑签名");
    }

    /// <summary>签名条下方的三行信息（参考图：标签带冒号、数值右对齐、金色细线分隔）</summary>
    private static void BuildInfoRows(RectTransform panel)
    {
        string[] labels = { "生日：", "索拉等级：", "联觉等级：" };
        string[] fields = { "BirthdayValueText", "SolarLevelValueText", "ResonanceLevelValueText" };
        const float startY = -382f;
        const float rowHeight = 58f;
        const float textHeight = 30f;
        const float lineGapFromText = 14f;
        float width = PanelWidth - PanelPad * 2f;

        for (int i = 0; i < labels.Length; i++)
        {
            float y = startY - i * rowHeight;
            bool hasInfo = i == 1;

            TMP_Text label = NewText("Label" + i, panel, 18f, new Color(0.62f, 0.62f, 0.64f, 1f), TextAlignmentOptions.Left);
            Place(label.rectTransform, PanelPad, y, 200f, textHeight);
            label.SetText(labels[i]);

            TMP_Text value = NewText(fields[i], panel, 21f, TextMain, TextAlignmentOptions.Right);
            // 右对齐要用右上 pivot，否则框会从锚点往右长、跑到面板外
            PlaceRight(value.rectTransform, -(PanelPad + (hasInfo ? 30f : 0f)), y, 240f, textHeight);
            value.SetText("--");

            if (hasInfo)
            {
                Button info = NewIconButton("SolarLevelInfoButton", panel, _iconInfo, new Color(1f, 1f, 1f, 0.45f), 17f);
                PlaceRight((RectTransform)info.transform, -PanelPad + 1f, y + 6f, 17f, 17f);
            }

            // 分隔线画在两行之间（贴本行文字底部再让 24px），不压字
            Image line = NewImage(panel, "Line" + i, new Color(Gold.r, Gold.g, Gold.b, 0.2f), _line);
            Image lineImg = line;
            var lineRect = lineImg.rectTransform;
            lineRect.anchorMin = lineRect.anchorMax = new Vector2(0f, 1f);
            lineRect.pivot = new Vector2(0f, 1f);
            lineRect.anchoredPosition = new Vector2(PanelPad, y - textHeight - lineGapFromText);
            lineRect.sizeDelta = new Vector2(width, 1f);
        }
    }

    // ──────────────────────────────
    // 角色卡
    // ──────────────────────────────

    private static void BuildCardRow(RectTransform panel)
    {
        RectTransform row = NewRect("CardRoot", panel);
        Place(row, PanelPad, -572f, CardWidth * CardSlots + CardGap * (CardSlots - 1), CardHeight);

        for (int i = 0; i < CardSlots; i++)
        {
            Sprite head = i < CardHeadPaths.Length ? LoadSprite(CardHeadPaths[i]) : null;
            float x = i * (CardWidth + CardGap);
            BuildCard(row, i, x, head);
        }
    }

    private static void BuildCard(RectTransform row, int index, float x, Sprite portrait)
    {
        var go = new GameObject("Card" + index, typeof(RectTransform));
        go.transform.SetParent(row, false);
        EnsureCanvasRenderer(go);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, 0f);
        rect.sizeDelta = new Vector2(CardWidth, CardHeight);

        var bg = go.AddComponent<Image>();
        bg.sprite = _panel;
        bg.type = Image.Type.Sliced;
        bg.color = CardFill;
        bg.raycastTarget = true;

        var button = go.AddComponent<Button>();
        button.targetGraphic = bg;
        button.transition = Selectable.Transition.None;

        Image frame = NewImage(rect, "Frame", Gold, _cardFrame);
        frame.type = Image.Type.Sliced;
        Stretch(frame.rectTransform, 1f);

        // 卡片头像：图片本身就是圆形裁切，铺满圆框即可；未解锁位没有头像，用占位字形
        RectTransform avatar = NewRect("AvatarRoot", rect);
        Place(avatar, (CardWidth - 120f) * 0.5f, -14f, 120f, 120f);

        Image maskImage = NewImage(avatar, "Mask", White, _circle);
        Stretch(maskImage.rectTransform);
        maskImage.gameObject.AddComponent<Mask>().showMaskGraphic = false;

        RectTransform headRect = NewRect("Avatar", maskImage.transform);
        Stretch(headRect);
        EnsureCanvasRenderer(headRect.gameObject);
        var head = headRect.gameObject.AddComponent<Image>();
        head.sprite = portrait;
        head.color = White;
        head.raycastTarget = false;

        if (portrait == null)
        {
            // 空头像会被渲染成白色方块，直接关掉，用「?」占位
            head.enabled = false;
            TMP_Text glyph = NewText("Glyph", maskImage.transform, 46f, new Color(Gold.r, Gold.g, Gold.b, 0.45f), TextAlignmentOptions.Center);
            Stretch(glyph.rectTransform);
            glyph.SetText("?");
        }

        TMP_Text label = NewText("Name", rect, 18f, TextMain, TextAlignmentOptions.Center);
        var labelRect = label.rectTransform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 0f);
        labelRect.pivot = new Vector2(0.5f, 0f);
        labelRect.offsetMin = new Vector2(6f, 14f);
        labelRect.offsetMax = new Vector2(-6f, 40f);
        label.SetText(index < CardNames.Length ? CardNames[index] : "未解锁");

        Image underline = NewImage(rect, "Underline", Gold, _bar);
        var underlineRect = underline.rectTransform;
        underlineRect.anchorMin = new Vector2(0.5f, 0f);
        underlineRect.anchorMax = new Vector2(0.5f, 0f);
        underlineRect.pivot = new Vector2(0.5f, 0f);
        underlineRect.anchoredPosition = new Vector2(0f, 7f);
        underlineRect.sizeDelta = new Vector2(CardWidth - 52f, 3f);
    }

    private static void BuildPanelFooter(RectTransform panel)
    {
        float width = PanelWidth - PanelPad * 2f;

        TMP_Text status = NewText("StatusText", panel, 15f, TextDim, TextAlignmentOptions.Left);
        var statusRect = status.rectTransform;
        statusRect.anchorMin = statusRect.anchorMax = new Vector2(0f, 0f);
        statusRect.pivot = new Vector2(0f, 0f);
        statusRect.anchoredPosition = new Vector2(PanelPad, 104f);
        statusRect.sizeDelta = new Vector2(width, 22f);
        status.SetText(string.Empty);

        Button edit = NewPillButton("EditButton", panel, new Vector2(width, 56f), new Color(0.95f, 0.94f, 0.91f, 1f));
        var rect = (RectTransform)edit.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(PanelPad, 32f);
        rect.sizeDelta = new Vector2(width, 56f);

        TMP_Text label = NewText("Label", edit.transform, 23f, Ink, TextAlignmentOptions.Center);
        Stretch(label.rectTransform);
        label.SetText("编辑影像簿");
    }

    // ──────────────────────────────
    // 右下角：特征码 + 隐藏界面按钮
    // ──────────────────────────────

    /// <summary>右侧保持「影像」本身（视频背景），只叠右下的特征码与隐藏按钮，不再压角色大图</summary>
    private static void BuildStage(RectTransform root)
    {
        // 特征码压在一块半透明深底上：视频里本身带 HUD 数字，直接压字会糊在一起
        var codeChip = NewImage(root, "StageCodeChip", new Color(0f, 0f, 0f, 0.55f), _panel);
        codeChip.type = Image.Type.Sliced;
        var chipRect = codeChip.rectTransform;
        chipRect.anchorMin = chipRect.anchorMax = new Vector2(1f, 0f);
        chipRect.pivot = new Vector2(1f, 0f);
        chipRect.anchoredPosition = new Vector2(-30f, 24f);
        chipRect.sizeDelta = new Vector2(224f, 42f);

        TMP_Text stageCode = NewText("StageCode", root, 18f, new Color(0.82f, 0.82f, 0.84f, 1f), TextAlignmentOptions.Center);
        stageCode.rectTransform.anchorMin = stageCode.rectTransform.anchorMax = new Vector2(1f, 0f);
        stageCode.rectTransform.pivot = new Vector2(1f, 0f);
        stageCode.rectTransform.anchoredPosition = new Vector2(-30f, 24f);
        stageCode.rectTransform.sizeDelta = new Vector2(224f, 42f);
        stageCode.SetText("特征码：--");
        AddShadow(stageCode);

        Button eye = NewCircleButton("EyeButton", root, 46f, new Color(1f, 1f, 1f, 0.22f), _circle);
        var eyeRect = (RectTransform)eye.transform;
        eyeRect.anchorMin = eyeRect.anchorMax = new Vector2(1f, 0f);
        eyeRect.pivot = new Vector2(1f, 0f);
        eyeRect.anchoredPosition = new Vector2(-268f, 22f);
        Image eyeIcon = NewImage(eye.transform, "Icon", new Color(1f, 1f, 1f, 0.9f), _iconEye);
        Stretch(eyeIcon.rectTransform, 11f);

        // 点右侧空白关闭（在视频之上、面板与标题之下）
        Button backdrop = NewButton("BackdropButton", root, new Color(0f, 0f, 0f, 0f));
        var backdropRect = (RectTransform)backdrop.transform;
        backdropRect.anchorMin = new Vector2(0f, 0f);
        backdropRect.anchorMax = new Vector2(1f, 1f);
        backdropRect.pivot = new Vector2(0.5f, 0.5f);
        backdropRect.offsetMin = new Vector2(PanelLeft + PanelWidth + 10f, 0f);
        backdropRect.offsetMax = new Vector2(0f, 0f);
        backdrop.transform.SetSiblingIndex(3);
    }

    // ──────────────────────────────
    // 基础工具
    // ──────────────────────────────

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Stretch(RectTransform rect, float inset = 0f)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    /// <summary>左上角定位：位置是距父节点左上角的偏移，尺寸给宽高</summary>
    private static void Place(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    /// <summary>右上角定位：x 取负值表示离右边多少，右对齐的框不会往右溢出</summary>
    private static void PlaceRight(RectTransform rect, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(x, y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Center(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void EnsureCanvasRenderer(GameObject go)
    {
        // Tuanjie 的 uGUI 分叉里 Graphic 基类不声明 RequireComponent(CanvasRenderer)，程序化创建要自己补
        if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
    }

    private static Image NewImage(Transform parent, string name, Color color, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        EnsureCanvasRenderer(go);
        var image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static TMP_Text NewText(string name, Transform parent, float size, Color color, TextAlignmentOptions align)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        EnsureCanvasRenderer(go);
        var text = go.AddComponent<TextMeshProUGUI>();
        if (_font != null) { text.font = _font; text.fontSharedMaterial = _font.material; }
        text.fontSize = size;
        text.color = color;
        text.alignment = align;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        // 单行固定文案用 Overflow：行框略矮时 Ellipsis 会把整段文字裁没
        text.overflowMode = TextOverflowModes.Overflow;
        return text;
    }

    private static void AddShadow(TMP_Text text)
    {
        var shadow = text.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.7f);
        shadow.effectDistance = new Vector2(1.2f, -1.2f);
    }

    private static Button NewButton(string name, Transform parent, Color color, Sprite sprite = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        EnsureCanvasRenderer(go);
        var image = go.AddComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.raycastTarget = true;
        var button = go.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None;
        return button;
    }

    private static Button NewCircleButton(string name, Transform parent, float size, Color color, Sprite sprite)
    {
        Button button = NewButton(name, parent, color, sprite);
        ((RectTransform)button.transform).sizeDelta = new Vector2(size, size);
        return button;
    }

    private static Button NewIconButton(string name, Transform parent, Sprite icon, Color color, float size)
    {
        Button button = NewButton(name, parent, color, icon);
        ((RectTransform)button.transform).sizeDelta = new Vector2(size, size);
        return button;
    }

    private static Button NewPillButton(string name, Transform parent, Vector2 size, Color color)
    {
        Button button = NewButton(name, parent, color, _pill);
        button.GetComponent<Image>().type = Image.Type.Sliced;
        ((RectTransform)button.transform).sizeDelta = size;
        return button;
    }

    private static void AddCrossMark(Transform parent, Color color, float length, float thickness)
    {
        for (int i = 0; i < 2; i++)
        {
            Image bar = NewImage(parent, i == 0 ? "BarA" : "BarB", color, null);
            RectTransform rect = bar.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(length, thickness);
            rect.localRotation = Quaternion.Euler(0f, 0f, i == 0 ? 45f : -45f);
        }
    }

    private static Sprite LoadSprite(string path)
    {
        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
        if (sprite == null) Debug.LogWarning("[ProfileView] 缺美术：" + path);
        return sprite;
    }
}
#endif
